# HQ Business+NG: how the account (party) master is written

Status: research spec, 9 Nov 2026. Sources: decompiled `ISP/FrmAccounts.cs` (FA), `ClsAccounts.cs`, `ClsDaybook.cs`,
`ClsInventory.cs`, `mdlGeneral.cs`; procedure text from `OBJECT_DEFINITION` in ISTMBM26 (identical names in ISTMP26);
live rows in ISTMBM26 / ISTMP26 (read-only). Line numbers refer to FA unless another file is named.

Only one interactive path matters: **FrmAccounts** (opened from the main menu, the chart of accounts, voucher entry and
report zoom). HQ has two others that call the same procedures (`FrmExcelToDatabase_Accounts` bulk import,
`FrmJobCard_Entry` quick customer) — not used by the shop and not specified here.

---

## 1. Write sequence

### 1.1 New account (`cmdSave_Click` 9720 → `SaveData` 9954 → `UpdateOpeningBalanceDifferenceAccount` 9746)

**No client transaction.** Every statement is its own auto-commit call on `mDataCn`; each procedure opens its own
`BEGIN TRAN`. `SaveData` catches every exception into a MsgBox, so a failure half-way leaves the earlier rows in place
and `UpdateOpeningBalanceDifferenceAccount` still runs afterwards.

| # | When | Statement | Evidence |
|---|---|---|---|
| 0a | form load | `select max(Act_Id) as result from IQAccMst` → code `"A" + (1000 + max + 1)` | `Gen_ActCode` 10176 |
| 0b | Save, validation | `Select Count(*) From IQAccMst where Act_name='<name without '>' and Act_Id<> 0` and the same for `Act_Code` | `IsDuplicateFound` 10134 |
| 1 | | `EXEC IQaccmst_insert` (26 params, §1.3). Inside its own tran: `INSERT IQAccmst`, then `EXEC IQUpdateRecOrder @@identity,0,@rec_higher` (tree fields, §2), commit, `@Act_id OUTPUT = @@IDENTITY` | 9962–9971 |
| 2 | images tab shown (`Hide_ActImagesTAB_Accounts=0` in both firms) | only if a picture was loaded: JPEG written to `<mAct_Images_FullPath><id>_ImageN.jpg`; file name goes to `Act_ImageN` | 9975, 11946 |
| 3 | | `Delete from IQAccadr where Act_Id=<id>` (text SQL) | 9979 |
| 4 | | `EXEC IQAccAdr_insert` (59 params, §1.4) | 9980–9981 |
| 5 | | `Delete from IQAccOthers where actID=<id>` then **10×** `EXEC IQAccOthers_insert(@mActID=id, @mActDegn, @mActName, @mActPhone, @mActEmailID, @mLnNo=0..9)` — one per grid row, blank rows included (`GrdOthers.RowCount = 10`, 9944) | `SaveAccount_OthersData` 11207 |
| 6 | Income/Expense only | `Save_AccBdg` → `IQaccbdg_insert` (budgets). Never for parties | 9983 |
| 7 | | `delete from IQDayBook where act_ID = <id> and div_no=<branch> and trdate<'01/Apr/2026'` (`GetACNT003`, ClsInventory.cs:35) | `SaveOpeningBalances` 10013 |
| 8 | not bill-wise (always so far) | `EXEC IQdaybook_insert` — one opening row, **even when the amount is 0** (§1.5) | 10035 |
| 8' | bill-wise opening ticked (`Chk_OPbal`, Customer only) | one `IQdaybook_insert` per grid row with series+number+amount>0 (§1.5) | 10014–10033 |
| 9 | | `Select isnull(sum(case when Type in('D','d') then Amount else (-1)*Amount End),0) from IQdaybook INNER JOIN IQAccMst ON ... where Type In('D','d','C','c') and IQAccMst.Act_ID<>5 and (Div_no=<b> or 0=<b>) and Doc_Id=-1` | 9753 |
| 10 | | `Update IQAccMst set Act_Opbal=<abs(sum)>,Act_opSign='<C if sum>0 else D>'  where Act_Id=5` | 9769 |
| 11 | | `Delete from IQdaybook where (Div_no=<b> or 0=<b>) and Act_Id=5` (all Act 5 rows) | 9770 |
| 12 | | `EXEC IQdaybook_insert` Act 5 "opDif" row (§1.5) | 9784 |
| — | | UI only: chart-of-accounts tree node add (`AddIn_Chart_of_Acccounts` 10051), autocomplete reload | |

`<b>` = `mBranchId`: 0 (Mainfrm 14641 uses `min(Branch_ID)` only when branches are enabled; `Branch` table is empty
in both firms). FY start = `mFyStartDate` (company master `Fy.StartDt`; = `HqFirm.PeriodStart`, 2026-04-01).

**Nothing else is touched**: no triggers on any table (sys.triggers empty), no `User_*` audit copy for accounts (only
document tables have `User_*`), no change-log / sync table. Other HQ PCs simply re-query `IQAccmst` when a form opens.

### 1.2 Modify (same `SaveData`, `mAct_Id > 0`)

Identical sequence except step 1 is `EXEC IQaccmst_update` with the same 26 parameters (`@Act_id` = existing id) and
the form fields are first loaded by `EXEC EditAccount @mAct_ID, @mBranchID` (`Fill_EditData` 10545).
`IQaccmst_update` (own tran):
* recomputes `Act_MG` from the (possibly new) parent; unknown parent → `Rec_higher=1, Act_MG='A'`;
* `UPDATE` every passed column — **including `Ent_Date` = today and `Ent_UId` = 1** (so Ent_Date = date of last save;
  confirmed: 11044 edited 8 Nov has `Ent_Date=20261108`);
* if the group changed (`@oldgroup<>@rec_higher and @oldgroup>0`): `EXEC IQUpdateRecOrder @act_id,0,@rec_higher`;
* if the name changed: `update IQNrMast set Act_name=new where Act_name=old`, and (HQ bug: tests `IQdepr`, updates
  `depr`). `@old_accname` is `varchar(30)` so long names always look "changed".

Steps 3–12 then **delete and re-insert** the address, the 10 contact rows and the opening row, and rewrite Act 5.
Consequences an imitation must reproduce (all verified on the 9 accounts edited 1–8 Nov):

* `IQAccAdr` is rebuilt from the form: values were loaded with `.Trim()` (10612–10716), so trailing spaces are lost,
  empties become `' '` again (CStr1, §5), `Act_FoodLICExpDate` = login date of the edit, `Cash_Discount` → NULL (not
  a parameter), `Act_Photo` → `''`.
* The opening `IQDayBook` row gets a **new SR_NO**, `Ent_Date=getdate()`, `BL_Adjusted=0`. Any `ADJUST` rows that
  settled that opening row now point at a deleted SR_NO (64 ADJUST rows in MBM26 reference opening rows; 778 ADJUST
  rows are already dangling). Prototype deliberately did an in-place `UPDATE` instead — see §6.
* Zero opening balance on edit → sign **'D'** whatever the type (10604: `Amount >= 0 → Dr`), so an edited zero-balance
  supplier flips `Act_OpSign` and the opening row's `TYPE` from C to D (10613 "K v n TRADERS" shows this).
* `Act_OPbal` is `abs(sum of the account's Doc_Id=-1 rows)` as loaded (`txtOPbal.Tag` 10588).

### 1.3 `IQaccmst_insert` / `IQaccmst_update` parameters (ClsAccounts.Insert_Accmst; call at 9962)

| Param (C# type) | Column | Value HQ sends (new party) | Source |
|---|---|---|---|
| `@Act_ID` Int InOut | Act_Id | 0 (insert) / id (update); output = new id | identity |
| `@act_name` VarChar(145) | Act_Name nvarchar(200) | `txtName.Text` with `'`→`''`, **not trimmed** (trailing blanks kept: "PASILA VEEDI KALYANAMANDAPAM "). Key handler upper-cases a first lowercase letter and blocks a leading space (10200). MaxLength 100. | user |
| `@act_type` VarChar(23) | Act_Type | "Customer" / "Supplier" (also Customer-Supplier, Asset, Bank, Cash, Expense, Income, Liability, Share Holder, Farmer, Broker) | user |
| `@Rec_higher` Int | Rec_Higher | `cmbActGrpUnder.SelectedValue`; type change auto-selects "Sundry Debtors"(414) for Customer, "Sundry Creditors"(407) for Supplier (9499/9505) — same ids in both firms | user (defaulted) |
| `@Act_Code` VarChar(60) | Act_Code | `txtCode` minus `'` ; default `A{1000+max(Act_Id)+1}` (= `A{1000+newId}` when no gap) MaxLength 10 | derived |
| `@Act_opbal` Decimal | Act_OPbal numeric(18,2) | `abs(opening)` from `txtOPbal.Tag` (set on Leave; null → CVal → 0) | user |
| `@Act_opSign` VarChar(5) | Act_OpSign | first char of "Dr"/"Cr". Defaults: Customer/Asset/Expense/Cash/Bank → D, others → C (9519) | user (defaulted) |
| `@Act_Cat` VarChar(13) | Act_Cat | 'A' (account) / 'G' (group) | constant |
| `@Act_TT` VarChar(25) | Act_TT | Customer or Supplier → the type text; else `cmbTradeType.Text` (Customer-Supplier → "" or stale text) | derived |
| `@Act_CA` Int | Act_CA | 0 | constant |
| `@Act_OpSplit` TinyInt | Act_OpSplit bit | `Chk_OPbal` (bill-wise opening) 0/1 | user, always 0 |
| `@Act_St` Int | Act_ST | `ChkBill2BillAdj` 0/1 (yes, Act_ST carries the bill-to-bill flag) | user, always 0 |
| `@Act_BillMode` TinyInt | Act_BillMode int | `cmbBillmode.SelectedIndex` of 0-Cash,1-Credit,2-Card,3-Others,4-Phone Pe,5-G Pay,6-PayTM,7-Others1; default **1** (9149) | user (defaulted) |
| `@Ent_Date` Int | Ent_Date | `yyyyMMdd` of PC date `DateAndTime.Today` | derived |
| `@Ent_uid` Int | Ent_Uid smallint | **1 hard-coded** (not the logged-in user) | constant |
| `@Act_usage` TinyInt | Act_Usage | `ChkActive ? 0 : 1` | user, 0 |
| `@Act_ControlID` | Act_ControlID | control customer only when bill mode = 3, else 0 | 0 |
| `@mCust_Type` tinyint | Cust_Type | index of None/Whole Saler/Retailer/Stockist/Doctor/M.R.P. | 0 |
| `@mBill2Bill` tinyint | Bill2Bill | `ChkBill2BillAdj` 0/1 (same as Act_ST) | 0 |
| `@mSalesmanID` int | SalesmanID | combo value | 0 |
| `@mIsStaff` smallint | IsStaff | 0 (staff box hidden, `ShowStaff_Attendance=0`) | 0 |
| `@mShift_ID` int | Shift_ID | 0 | 0 |
| `@mActTaxType` tinyint | ActTaxType | index of Local/Central/Both, default **2** (9150) | 2 |
| `@mIsTCSAPPLY` smallint | IsTCSAPPLY | 0 | 0 |
| `@mTCSApplyAmt` float | TCSApplyAmt | 0 (never passed → default) | 0.00 |

Columns not in the INSERT: Rec_Level/Rec_Index/Rec_Order (set by `IQUpdateRecOrder`), Act_MG (proc: parent's Act_MG),
Act_PyBal NULL, Rpt_Sel NULL (report screens later run `update IQAccmst set Rpt_Sel=0` globally, which is why every
existing row shows 0), IsConv default 0, Bal NULL.

### 1.4 `IQAccAdr_insert` parameters (ClsAccounts.Insert_AccAdr; call at 9980)

`CStr1(x)` returns `" "` for `""`/DBNull (mdlGeneral.cs:2888). Whether HQ trims decides `' '` vs `''`:

| Column (param) | Value | Empty stored as | Observed use (20 newest MBM / 6 newest MP parties) |
|---|---|---|---|
| Person (`@person`) | contact person, `'`→`''` | `' '` | never |
| Add_Line1 / Add_Line2 | address lines, `'`→`''`, untrimmed, MaxLength 60 | `' '` | **Line 2 is the one used** (12/20); Line 1 2/20 |
| Act_Town | untrimmed, MaxLength 25 | `' '` | always ("PEDDAPURAM " with trailing space is common) |
| Act_District | MaxLength 25 | `' '` | 2/20 |
| Act_state | `cmbstate.Text` (§3.3) | n/a | always |
| Act_PinNo, Act_Ph1, Act_Ph2, Act_STD, Act_Email | | `' '` | never |
| Act_Cell | mobile, MaxLength 12 | `' '` | 2/20 |
| Act_CSTNo / Act_SSTNo / Act_VAT_TINNO | captions "Branch Name" / "Acc.No" / "IFSCode" (bank, `HideBnkDetails_in_CustSupp=0`) | `' '` | never |
| Act_TAN | caption "Bank Name"; **trimmed** | `''` | never |
| Act_PAN | **trimmed** | `''` | never |
| Act_Credit (float) | credit limit `Val()` | 0 | 0 |
| Act_CrDays (int → nvarchar) | credit days | `'0'` | 0 |
| Area_Id | area combo value | 0 | 0 |
| Act_Transport | | `' '` | never |
| Act_Mode (`@Act_Mode bit`) | `cmbBillmode.SelectedIndex` → bit (any non-zero → 1) | — | 1 (credit) |
| Act_Photo | literal `""` | `''` | — |
| Act_Rate (numeric) | `cmbRateType.SelectedIndex`, default 1 (Sale Rate) | — | 1.00 |
| Act_DL20B / Act_DL21B | untrimmed CStr1 | `' '` | never |
| ship_Person | **trimmed** | `''` | never |
| ship_Add_Line1 … ship_Act_Email (except state) | untrimmed CStr1 | `' '` | never |
| ship_Act_state | `CmbShipmentState.Text`, default company state (not driven by GSTIN) | — | "37-Andhra Pradesh" |
| AadharNo | trimmed | `''` | never |
| GSTIN | upper-cased (CharacterCasing.Upper, 7248) and trimmed | `''` | 8/20 MBM, 4/6 MP |
| GSTTaxType | index: 0 Regular/Registered, 1 Composition, 2 Un-Registered (default), 3 Others/Consumer | — | 0 or 2 |
| ship_Act_GSIN / ship_Act_PANNO | untrimmed | `' '` | never |
| Act_CrCtrlDays | `CVal(txtControlDays)` | 0 | 0 |
| ActFoodLICNO | trimmed | `''` | never |
| Act_FoodLICExpDate | **`mdlGeneral.mLogDate`** (HQ login working date, midnight) — always passed | — | = save date |
| Act_Note1..6, Act_Image1..4 | trimmed | `''` | never |
| Cash_Discount | not a parameter | NULL | NULL |

All string params are bound with `AddWithValue` (nvarchar) but the procedure declares them `varchar`, so non-Latin text
becomes `?` even though the columns are nvarchar. Same for `@act_name` (VarChar(145)).

### 1.5 Opening rows: `IQdaybook_insert` (ClsDaybook.Daybook_Insert:17)

Opening row (10035), one per account per save:
`@sr_no=0 (OUT), @trdate=FyStart-1 (2026-03-31 00:00), @Act_id=id, @Amount=abs(opening), @type='D'|'C' (CmbSign index),
@Contra_id=0, @BL_Adjusted=0, @Adjusted=0, @Doc_ID=-1, @VType='opbal', @ARS=0, @ARS_Ord=0, @Div_no=0, @days_from=0,
@days_to=0, @int_credit=0, @Tseries='', @Tno=0, @Textn='', @discount=0, @doc_type=0, @Narration='', @Bill_Qty=0,
@ent_Uid=1, @adj_id=0, @lnno=0, @mDiscRow='', @mImportCollection_DtldID=0`; `@Salesman_ID`/`@TRDueDate` not passed →
proc defaults 0 / NULL (rows before HQ's ~4 Nov update have NULL Salesman_ID — the procedure itself changed).
Column defaults fill `Ent_Date=getdate()`, `IsAudit=0`, `Branch_id=0`; bondseries/TallyGUID NULL.

Bill-wise opening (10028): same but `@trdate` = grid bill date, `@Tseries`/`@Tno`/`@Textn` from the grid, amount abs,
type from grid Dr/Cr. Zero such rows exist in either firm (`Bill2Bill_Comp=1` so the box is visible, but unused).

Act 5 "Opening Balance Difference Account" row (9784): same shape, `@Act_id=5, @Amount=abs(sum), @type` ('C' when the
other opening rows net to debit, else 'D' — also for 0), `@VType='opDif'`. Today MBM26: 2,183,415.60 C, SR_NO 82347
written by the 8 Nov edit. It is deleted and re-inserted (new SR_NO) on **every** account save or delete.

---

## 2. Act_Id and tree fields

* **Act_Id**: `IQAccmst.Act_Id int IDENTITY` (only index: clustered PK). MBM26 ident 11319, MP26 10304.
* **Act_Code**: computed on the client when the form opens, from `max(Act_Id)` (not IDENT_CURRENT): `"A"+(1001+max)`.
  Two PCs opening the form together get the same code; the second save is refused by the duplicate-code check.
  19 duplicate codes already exist in MBM26 (legacy), so do not assume uniqueness when reading.
* **Act_MG** = parent group's Act_MG ('A' debtors, 'L' creditors).
* **Rec_Level / Rec_Index / Rec_Order** — `IQUpdateRecOrder(@m_Act_Id, @SrcGroup(unused), @TargetGrp)`:
  ```sql
  @T_LvlNo    = Max(Rec_level) where Act_id = @TargetGrp
  @T_RecIndex = Max(Rec_Index) where Rec_higher = @TargetGrp          -- new row still NULL, ignored
  @T_RecOrder = Max(Rec_Order) where Rec_higher = @TargetGrp or act_id = @TargetGrp
  update IQaccmst set Rec_index=@T_RecIndex+1, rec_level=@T_LvlNo+1 where Act_id=@m_Act_Id
  Update IQaccmst set Rec_order = Rec_order+1 where Rec_order > @T_RecOrder   -- renumbers many rows
  Update IQaccmst set Rec_order = @T_RecOrder+1 where act_id=@m_Act_Id
  ```
  So it is a **global ordered index with shifting**, not a nested set. Rec_Index = sibling ordinal (debtors at 713),
  Rec_Level = parent level + 1 (3 for parties). Rec_Order (`smallint`) = display order of the chart of accounts; a new
  debtor goes after the last *direct* child of 414 and every row above is shifted +1 (207 rows in MBM26 today for a debtor,
  178 for a creditor). Only direct children are considered, so the order is not a true pre-order (creditors 965–969
  sit far from their group at 25); duplicates already exist (1124 distinct of 1152). Verified against the last 20
  parties (Rec_Index and Rec_Order strictly consecutive per group, shifts consistent). A group with no children gives
  `NULL+1 = NULL`. HQ's own GST-account generator left 14 rows in MP26 with NULL tree fields, so HQ tolerates NULLs.
* **Uniqueness checks** (`IsDuplicateFound` 10134): only name and code, string-built SQL with `'` stripped,
  `SQL_Latin1_General_CP1_CI_AS` → case-insensitive, trailing blanks ignored. **No GSTIN or mobile uniqueness**
  (12 duplicate GSTINs exist in MBM26). Note the check strips `'` while the save doubles it, so a name containing `'`
  is stored with `''` and is never matched (no such names exist today).
* **Other validations**: name and code non-empty; for non Cash/Bank types, GSTIN required when GST type is
  Registered/Composition (10117); a non-empty GSTIN must be exactly 15 chars and GST type must be Registered or
  Composition (`IS_Valid_GSTIN` 9683). The checksum (`GSTINValidator.IsValid`) only sets a label — invalid GSTINs save.

---

## 3. What the shop actually enters

### 3.1 Column classification (20 newest parties MBM26: 17 customers, 3 suppliers, 27 Jul–4 Nov; ISTMP26's 20 newest
are 14 HQ-generated GST tax accounts + 6 parties, same pattern)

* **User-entered**: Act_Name, Act_Type, Add_Line2 (sometimes Line1), Act_Town, Act_District (rare), Act_Cell (rare),
  GSTIN + GSTTaxType, Act_state (auto from GSTIN), opening balance+sign (0 for every new party in MBM; non-zero
  exists in MP), group (auto from type).
* **Derived**: Act_Id, Act_Code, Act_MG, Rec_Level, Rec_Index, Rec_Order (+ shifts), Act_TT, Ent_Date,
  Act_FoodLICExpDate, Act_Mode (=bill mode≠0), IQDayBook opening + Act 5 rows, Act 5 master balance.
* **Constant for this shop**: Act_Cat 'A', Act_CA 0, Act_OpSplit 0, Act_ST 0, Bill2Bill 0, Ent_Uid 1, Act_BillMode 1,
  Act_Usage 0, Act_ControlID 0, Cust_Type 0, SalesmanID 0, IsStaff 0, Shift_ID 0, ActTaxType 2, IsTCSAPPLY 0,
  TCSApplyAmt 0.00, Act_PyBal/Bal NULL, IsConv 0, Rpt_Sel NULL-then-0; IQAccAdr as in §1.4 (all blanks `' '`/`''`
  exactly as listed, Act_Rate 1.00, Act_Credit 0, Act_CrDays '0', Area_Id 0, Cash_Discount NULL, ship_Act_state
  company state); IQAccOthers 10 blank rows LnNo 0–9 (all 263 form-saved accounts have exactly 10; 3 of 2630 rows
  are non-blank).

### 3.2 Inputs a new C# form needs
Required: name, type (Customer / Supplier), town. Usual: address (store in **Add_Line2** to match habit, or Line1 —
both legal), GST type (Registered / Unregistered; Composition and Consumer are legal but unused), GSTIN (when
registered), state (auto), mobile. Optional: district, PIN, phone, email, opening balance + Dr/Cr, bill mode
(default Credit), group override, active flag, credit limit/days, contact person, PAN, Aadhaar, bank details.

### 3.3 State / GSTIN / GST type
* Form default: `Act_state` = company state (`[company master].State`, else "37-Andhra Pradesh"); same for ship state.
* `Verify_GSTIN` (12161) runs on GSTIN text change and GST-type change, only for Customer/Supplier/Customer-Supplier
  and only when GST type ≤ 1: if the GSTIN matches HQ's pattern, the state combo jumps to the item whose first two
  characters equal the GSTIN's first two. If the pattern fails, `IsValid` throws and the state is left alone.
* Registered party: GSTTaxType 0, GSTIN 15 chars upper, Act_state = GSTIN's state (11300: GSTIN 36…, state
  "36-Telangana", ship state still "37-Andhra Pradesh").
* Unregistered party: GSTTaxType 2, GSTIN `''`, Act_state = whatever the user leaves (default AP); another-state
  unregistered party → user picks the state by hand.
* The state captions must be **FrmAccounts' list** (7467), e.g. "01-Jammu & Kashmir", "25-Daman & Diu",
  "26-Dadra & Nagar Haveli", "31-Lakshdweep", "34-Pondicherry", "35-Andaman & Nicobar Islands", "36-Telangana",
  "37-Andhra Pradesh", "97-Other Territory"; no 28 or 38. `MK.Hq.Domain.Gst.Gstin.States` uses different captions
  (e.g. "Jammu and Kashmir", "Puducherry", 38-Ladakh) — do not use it to write Act_state.
* CGST/SGST vs IGST on bills comes from the transaction type the user chooses (TTYPES.Firstor, e.g. series IS = IGST
  sales for 11300), not from Act_state.

---

## 4. Delete (`cmdAnother_Click` "&Delete" branch 10349)

Refused (MsgBox, nothing written) when any of these is non-zero:
child accounts (`IQAccMst.Rec_Higher=id`); `IQDaybook` rows with `Act_Id=id or Contra_Id=id` **and Amount>0** (so a
non-zero opening balance blocks deletion); `IQDoc_Header.Act_Id`; `IQNrMast.Act_Id`; `IQCHARGES.Act_Id`;
`IQDOC_Recd.Act_Id or Tax_ID`; `IQDOC_Delv.Act_Id or Tax_ID`; `Ttypes` Cash_naccno/Bank_naccno/Exempt/Gross*/Tax*
account columns; `IQItems_Mast` Vip/Vop/CGST*/SGST* account columns; `Vehicles.Act_Id`. Then a Yes/No prompt.
Not checked: `IQDoc_Header.Act_ControlID`, budgets, ADJUST.

Deletes, in order, each auto-commit: `Delete From IQAccadr where Act_id=id`; `Delete From IQAccMst where Act_id=id`;
`Delete From IQdaybook where Act_id=id` (only zero-amount rows can remain, i.e. the 0 opening row);
`Delete From IQAccOthers where Actid=id`; image files deleted (`Delete_ActImages`); then steps 9–12 (Act 5 rewrite).
No Rec_Order compaction (gap left). Accounts 1–8 have name/type/group locked in the form (9250). The
`delete_account` procedure exists but HQ code never calls it.

---

## 5. What would make a foreign row look different or break HQ

1. **Blank encoding**: `' '` vs `''` per column exactly as §1.4 (CStr1 rule). NULL only where HQ leaves NULL
   (Cash_Discount, Act_PyBal, Bal, Rpt_Sel on a fresh row). Numbers as typed: Act_CrDays is text `'0'`, Act_Rate 1.00.
2. **No trimming** of name/town/address (trailing blanks are part of HQ's own data; keep what the user typed, but
   an edit by HQ will later trim the address fields — not the name).
3. Apostrophes: HQ stores `'` doubled in Act_Name and the address fields; Act_Code drops them. Decide whether to
   copy this (parity) or forbid `'` in input (recommended: forbid, no existing data has one).
4. Non-Latin characters become `?` in HQ (varchar params). Restrict input to Latin/ASCII to stay identical.
5. **Ent_Uid = 1** and opening-row `ent_Uid = 1` regardless of user.
6. Always write the zero opening row and always rewrite Act 5 (master + opDif row). Skipping it leaves Act 5 stale
   until the next HQ account save — harmless to HQ, but visibly different.
7. Run `IQaccmst_insert` (do not INSERT directly) so the Rec_Order shift happens; it renumbers hundreds of rows.
8. Act_FoodLICExpDate must be the save date (HQ login date) — HQ passes it on every save.
9. Exactly 10 IQAccOthers rows LnNo 0–9 with `''` values.
10. Act_state/ship state captions from FrmAccounts' list, GSTIN upper-case trimmed, GSTTaxType 0/2 consistent with
    GSTIN presence (HQ refuses to save GSTIN with type 2, and type 0/1 without GSTIN).
11. Concurrency: HQ has no lock between the code read, the duplicate check and the insert. We should run the whole
    sequence in one transaction with an applock + `UPDLOCK,HOLDLOCK` on IQAccmst and compute Act_Code from
    `max(Act_Id)` inside it; HQ readers will not notice the difference.

---

## 6. Cross-check with the TypeScript prototype (`core/packages/hq-gateway/src/native-accounts.ts`)

Agrees with HQ: procedure set and order of IQAccmst → IQAccAdr (delete+insert) → IQAccOthers (10 rows) → opening
`IQdaybook_insert`; code `A{max+1001}`; ActTaxType 2, Act_Rate 1, `' '` blanks list, `''` for PAN/TAN/GSTIN/Aadhar,
ship state AP, FoodLICExpDate = save day, opening row shape (`VType 'opbal'`, FyStart−1, Doc_ID −1, ent_Uid 1).

Disagrees with HQ (HQ is right for parity):
* **Never rewrites Act 5** (steps 9–12 missing) — real gap.
* `Ent_UId` = caller's HQ user id; HQ always sends 1.
* Edit: updates the opening row in place (keeps SR_NO and ADJUST links); HQ deletes and re-inserts (new SR_NO, orphaned
  settlements). This is a conscious choice — keep the prototype's safer behaviour only if the user accepts the
  difference; otherwise replicate HQ and warn when ADJUST rows reference the opening row.
* Edit: preserves stored `Cash_Discount`, FoodLICExpDate and untouched address text; HQ nulls Cash_Discount, sets
  FoodLICExpDate to the edit date and trims every address field.
* Edit with zero opening: keeps stored sign; HQ forces 'D'.
* Does not double `'` in names.
* Rewrites IQAccOthers on edit only if contacts changed (HQ always rewrites; same final content).
* GST check requires `state[0..2] == gstin[0..2]`; HQ does not enforce it (12 mismatches exist) but auto-sets it.

## 7. Side finding (existing engine)
`Catalogue.PartiesAsync` balance = `Act_OPbal (signed) + SUM(all IQDayBook rows)`; the IQDayBook sum already contains
the `Doc_Id=-1` opening row, so every opening balance is counted twice (e.g. 11044: 160,190 C appears in both). HQ's
own balance reads (`EditAccount`, `GetINT001`) use IQDayBook only.

## 8. Open questions
* `mLogDate` vs PC date: HQ uses the login working date for Act_FoodLICExpDate and the PC date for Ent_Date; they
  only differ if someone logs in with a back date. We use the save date for both.
* Bill-wise opening (`Act_OpSplit=1`) and Customer-Supplier `Act_TT` were not exercised by the shop; treat as
  unsupported until a real HQ row exists to compare against.
* `IQUpdateRecOrder` for an empty target group yields NULL Rec_Index/Rec_Order — refuse new groups or empty targets.
* The company-master database (`[company master]`, `Fy`) is not on this server; company state and FY start are taken
  from `FirmProfile.StateCaption` / `HqFirm.PeriodStart` and confirmed by data (ship state AP, opening TrDate 2026-03-31).
