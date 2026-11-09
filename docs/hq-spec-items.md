# HQ Business+NG — Item master write spec

Status: research complete 2026-11-09 (read-only; decompiled source + live ISTMBM26 / ISTMP26).
Scope: what HQ writes when an item is **created**, **modified**, **deactivated** or **deleted** from the
Items screen (`FrmItems`), so `mk-hq` can produce rows HQ cannot tell apart from its own.

Source refs are `ISP\<file>:<line>` under `C:\MK_WORKSPACE\Context\HQ_Decompiled\src\ISP`.

---

## 0. Summary

* Create = 1 + 1 + 1 + 1 + N calls on **separate connections with no outer transaction**
  (`ClsMySQlHelper.ExecuteNonQuery` opens/closes per call, `ClsMySQlHelper.cs:18`); each proc has its own
  `BEGIN TRAN`. N = number of rows in the TTypes query (16 in ISTMBM26, 15 in ISTMP26).
* `Item_ID` = IDENTITY on `IQItems_Mast` (proc returns `@@identity`; no triggers exist in the DB).
  `IQItems_Units.ItemUnitsDtldID` = IDENTITY. `Ttype_Acnt` has PK (Ic_Id, TT_ID).
* `Item_Code` default = `Getmaxitemcode` (type 0 = `COUNT(Item_ID)+1`); `Item_Ord` = `MAX(ISNULL(Item_Ord,0))+1` in the group.
* No audit (`User_*`) copy, no change-log/sync/version table, no HSN_Master write, no opening stock on this screen.
  Other PCs simply re-query. (`User_IQ*` tables exist only for documents; `SaveUserchangesAuditDatabase=0`.)
* There is no soft-delete beyond the "Active" checkbox (`Item_Stat=1`). Hard delete via `Items_delete` is refused
  once the item is used in any document.

---

## 1. Write sequence

### 1.1 Create (`CmdSave_Click`, `FrmItems.cs:9183-9312`, `mItem_Id = 0`)

| # | Call | Notes |
|---|------|-------|
| 0 | validations: `IsEmptyFound()` `:9426`, `IsDuplicateFound()` `:9516` | see §2.3 |
| 1 | `SELECT Act_ID FROM IQaccmst WHERE Act_Name='<grid name>'` per TTypes row (`SetAccountID_To_Grid`, `:12392`) | re-resolves the Ttype account id from its **name** |
| 2 | `select Max(isnull(Item_ord,0))+1 from IQitems_mast where grp_ID=<Grp_ID>` (`getItemOrd`, `:9160`) | empty group → NULL → `CVal(DBNull)` = **0** |
| 3 | `EXEC IQItems_Mast_insert` (§3.1) with `@mItem_ID=0` (Int, InputOutput) | INSERT; output `@mItem_ID=@@identity` |
| 4 | `EXEC IQItems_Mast_Update_ItemImages @mItem_ID, '', '', '', ''` | only when `Configs.Hide_ItemImagesTAB_Items=0` (true in both firms). Sets Item_Image1..4 = `''` (not NULL) |
| 5 | `Delete from IQItems_Units where Recd_ID=0 and Item_ID=<id>` (plain text SQL) | no-op on create |
| 6 | `EXEC IQItems_Units_insert` (§3.2) | always one row, UNit_Id=3 |
| 7 | `EXEC Ttype_Acnt_insert @ic_id,@tt_id,@act_id,@set_no,@ent_date,@Ent_uid,@Edmode=0` once per grid row, in TTypes query order (§4) | proc = `DELETE ... WHERE ic_id,tt_id` then `INSERT` |
| 8 | (BOM: only if `Configs.Enable_BOM>0` — 0 in both firms) | skip |

If step 3 returns ≤ 0 rows HQ shows "Record Insertion Failed" and stops; failures in 4–7 just show a MsgBox and
continue (partial item possible). **Recommendation:** run 2–7 in one SQL transaction in mk-hq (outcome identical;
the procs' inner `BEGIN TRAN/COMMIT` nest fine; an inner `ROLLBACK` would abort the outer tran, which is what we want).

### 1.2 Modify (same `CmdSave_Click`, `mItem_Id > 0`)

Identical call list with `@mItem_ID=<id>`. Differences:

* `IQItems_Mast_insert` takes the **UPDATE** branch: every column in the INSERT list is overwritten **except
  `Item_Ord` and `Rpt_Sel`** (getItemOrd is still executed but ignored). `Ent_Date` and `Ent_Uid` ARE overwritten →
  `Ent_Date` is "last saved", not "created" (e.g. 105 old items show 20261005; only ~3 items were actually created in Oct 2026).
  Columns never touched by the proc: Item_Ord, Rpt_Sel, ThridPack_Cap, ThridPack_Value, Allow_BOM, Cost_Calculated,
  ItemGSTTYPE, Stk, LastPLUCode (images via step 4).
* Step 5 deletes the `Recd_ID=0` unit row and step 6 re-inserts it → **new `ItemUnitsDtldID`** every save.
* Step 7 rewrites every TTypes row (delete+insert) with new `Ent_Date`/`Ent_uid`. Account per row = the stored
  `Ttype_Acnt` account when one exists with `Act_ID>0` (`Fill_EditData_TtypeAcnt`, `:9706`), otherwise the
  rate-derived account (§4). If the user **changes the GST rate**, `cmbvat_SelectedIndexChanged` → `Set_GSTAccounts`
  recomputes **all** grid rows and the six tax-account combos from the new rate. Ttype rows whose TT_ID is no longer
  in TTypes are left as they are.
* All master fields are re-sent from the form, which was loaded from the row (`Fill_EditData_Items`, `:9590`) with
  `.Trim()` on strings and `CVal` on numbers. So a modify normalises: NULL text → `' '`/`''` per §5, NULL numbers →
  0 (this is why columns added in later HQ builds are NULL only on never-re-saved items).
  Accounts not present in their combo list come back as **0** (observed: old items keep `GSTCess_ActID=0`).
* Unit row hidden fields (PGRATE, PDRATE, …) are reloaded from the old row (`Fill_EditData_Units`, `:9764`). Decompiled bug:
  `mSCTAX` is loaded from `SSTAX` and `mSSTAX` is never loaded (all are 0 in data, so harmless).
* Pack combo is disabled once `Items_TransactionsMade` finds the item in a document; **Item_Code stays editable**
  (disabled only when PLU codes are enabled). Name and group stay editable.

### 1.3 Deactivate
No separate action: untick "Active" and save → `Item_Stat=1` (DeActDate stays 0, DeActReason `''`). Effect: hidden from
`ItemsFindlist_Inward` (`isnull(Item_Stat,0)=0`). No inactive items exist in either firm.

### 1.4 Delete (`CmdDelete_Click` `:15047`; same from list screen `FrmListofItems.cs:1594`)
`EXEC Items_delete @mITem_ID=<id>, @mStatus OUTPUT` (SmallInt in C#, int in proc). Status set (last match wins):
1 in `IQDoc_REcd`, 2 `IQDoc_Delv`, 3 `IQPOint_Trans`, 4 `IQOrd_Detail` (each joined to `IQDoc_Header`), 5 `Tanks`,
6 `Bunk_Header`, 7 `Bulk_Master` (Item_ID or BulkItem_ID). Status>0 → "Ralated Information Found. Delete can't possible."
Status 0 → deletes `IQItems_Units` (all Recd_IDs), `Item_VehicleServices`, `IQItems_BOM` (Main_Item_ID), `IQItems_Mast`;
then HQ deletes image files `<path><id>_Image1..4.jpg`. **`Ttype_Acnt` rows are NOT deleted** (238 orphan rows exist in
ISTMBM26) — mk-hq must leave them too to match HQ.

---

## 2. Allocation and uniqueness

### 2.1 IDs
* `Item_ID`: `IDENTITY` (`IDENT_CURRENT` = MAX = 2531 in ISTMBM26). Read the proc's output param (= `@@identity`;
  no triggers, so equal to `SCOPE_IDENTITY()`).
* `ItemUnitsDtldID`: IDENTITY, never read by HQ.
* `Item_Ord` (smallint): §1.1 step 2; per Grp_ID; computed **before** the insert, no lock (duplicates across groups normal).

### 2.2 Item code
* Default text = `EXEC Getmaxitemcode @max_id OUTPUT (BigInt in C#, varchar(50) in proc), @mItemCode_GenerateType=Configs.ItemCode_AutoGenerateType`
  (`GetMaxItemID`, `:12272`); 0 → 1. Both firms use type 0 = `COUNT(Item_ID)+1` (types: 1 = MAX(Item_ID)+1001,
  2 = MAX(ISNUMERIC(code))+1 [sic], 3 = MAX(getFirstNumeric(code))+1, 4 = manual).
* Observed: staff append 1–2 digits to the default (ISTMBM26 item 2531 → `248525` = 2485‖`25`; ISTMP26 407 → `4001` = 400‖`1`).
  **Open question:** meaning of the suffix; treat as user-typed.
* `CodeAlias` = `txtBarcode`, which mirrors `txtCode` whenever `Configs.ShowCodeAlias=0` (`txtCode_TextChanged`, `:14939`) →
  `CodeAlias = Item_Code` for every recent item.
* Count-based default can collide after deletions; the duplicate check catches it.
* `txtCode_KeyPress` upper-cases a–z and rejects a leading space (decompiled flow is mangled; low confidence on exact rule).
  `txtName_LostFocus`: if code empty, code = first 3 chars of name.

### 2.3 Validations (in order)
`IsEmptyFound` (`:9426`): name, code, purchase pack (`txtPack`), sale pack (`txtSalePack`) non-blank after Trim; Grp_ID≠0;
if GST>0 then IGST input (`VIP_ID`) and IGST output (`VOP_ID`) >0; IssueConv>0; if UPPER(TRIM(pack))≠UPPER(TRIM(salepack))
then IssueConv≠1; cess>0 needs cess a/c; addl cess>0 needs its a/c.

`IsDuplicateFound` (`:9516`), all with `AND Item_Id<>@current`:
* name: `Select Count(*) From IQItems_mast where Grp_Id=<grp> and Item_name='<Replace(Trim(name),"'","")>'` →
  **scoped to the group**; collation `SQL_Latin1_General_CP1_CI_AS` → case-insensitive, trailing spaces ignored;
  apostrophes are stripped from the probe only, so a name containing `'` never matches an existing one (10 such items).
* code: `Item_Code='<Trim(code) without '>'` — global, case-insensitive.
* code alias: only when `ShowCodeAlias>0` (not in these firms): `ltrim(isnull(Codealias,''))='<alias>'`.
* uniform code: when non-blank and ≠ `'0'`: `ltrim(isnull(UniformCode,''))='<uc>'`.
* No unique constraints back these (only PK_Items; non-unique index on Item_Name, Item_Code). Currently 0 duplicate
  (group,name) and 0 duplicate codes in ISTMBM26.

---

## 3. Exact procedure calls

Bind with the **same SqlDbTypes HQ uses** (they drive SQL's implicit conversions): `long`→BigInt, `int`→Int,
`byte`→TinyInt, `double`→Float, `string`→NVarChar (AddWithValue), `CVal(...)` results → Decimal.

### 3.1 `IQItems_Mast_insert` (`ClsItems.cs:17`; call `FrmItems.cs:9202`)

Proc param types in brackets. "Value" = what HQ sends on create in these firms; `CStr1(x)` returns `" "` for `""`
(`mdlGeneral.cs:2888`), `CVal(x)` = Trim, strip commas, `""`/DBNull→0, Decimal (`mdlGeneral.cs:2703`).

| Param [proc type] | HQ source | Class | Value / rule |
|---|---|---|---|
| @mItem_ID [int OUTPUT] | mItem_Id | derived | 0 on create; id on modify |
| @mItem_Name [varchar(70)] | `CStr1(txtName).Trim()` (MaxLength 70) | user | trimmed; >70 truncated by proc |
| @mItem_Code [varchar(45)] | `CStr1(txtCode).Trim()` (MaxLength 30) | user/default | §2.2 |
| @mItem_Pack [varchar(30)] | `CStr1(txtPack)` **not trimmed** (MaxLength 15) | default | new item pre-filled from `Item_Pack` of MAX(Item_ID) item (`Set_Defaults`, `:8790`) → `KGS` (MBM), `1 PIC` (MP) on every recent item |
| @mGrp_ID [int] | cmbCompany (IQGroups) | **user** | required (the "company/manufacturer" group) |
| @mGen_ID [int] | cmbCat2 (IQItems_Gen) | const | 0 (divisions hidden) |
| @mCat_ID [int] | cmbCat (IQItems_Cat) | user (rare) | 0 normally (34/12/25 seen) |
| @mRack_No [varchar(20)] | `CStr1(cmbRackno.Text)` untrimmed | const | **`' '` (one space)** |
| @mSch_Drug [varchar(50)] | `CStr1(CmbSch_TYpe.Text)` untrimmed | const | **`' '`** |
| @mVAT [numeric(9,2)] | `Val(cmbvat.Text)` | **user** | GST % (DropDownList from `Fill_ItemGSTRates_From_SaleTypes`); new item defaults to VAT of MAX(Item_ID) item |
| @mCST | 0.0 | const | 0 |
| @mDis_Recd [numeric(9,2)] | Val(txtDisRecd) | const | 0 |
| @mAllow_Disc [tinyint] | chk "check disc as max" | const | 0 |
| @mDis_Issd | Val(txtDisIssed) | const | 0 |
| @mPrimary_PackID [int] | `GetPackIsZero_Set3(CmbPack)` (0/null→3) | const | **3** (UOM 3 = `PC`) |
| @mInc_Tax [tinyint] | ChkInclu_tax (designer default true) | const | **1** |
| @mPur_PackID / @mSal_PackId / @mRpt_PackID | GetPackIsZero_Set3(...) | const | **3** |
| @mMin_Qty / @mMax_Qty [numeric(9,2)] | CVal | const | 0 |
| @mCST_PActID, @mCST_SAcID, @mPur_ActID, @mPurRet_ActID, @mSal_ActID, @mSalRet_ActID | literal 0L | const | 0 |
| @mVIP_ID / @mVOP_ID [int] | IGST input / output combos | derived | §4 |
| @mItem_Ord [smallint] | getItemOrd | derived | §2.1 (ignored on update) |
| @mItem_Stat [tinyint] | `chkActive ? 0 : 1` | const | 0 |
| @mDeActDate / @mDeActReason | 0L / `""` | const | 0 / `''` |
| @mEnt_Date [int] | `Date2Num(mLogDate)` = yyyyMMdd | derived | **HQ login date** (user-chosen at logon, `FrmLogon.cs:1027/1040`), not wall clock |
| @mEnt_Uid [smallint] | mUserId | derived | 1 in all rows |
| @mRpt_Sel [tinyint] | literal 0 | const | 0 |
| @mItem_Desc [varchar(8000)] | `CStr1(txtItemDesc).Trim()` | user (rare) | `''` |
| @mAllow_Batch | chk | const | 0 |
| @mCodeAlias [varchar(50)] | `CStr1(txtBarcode).Trim()` | derived | = Item_Code |
| @mbrand_ID / @mDept_ID [int] | combos (hidden) | const | 0 |
| @mSetDiscMRP_SaleRate | chk | const | 0 |
| @mSizeCol [varchar(50)] | `CStr1(txtsize).Trim()` | const | `''` |
| @mWgt [numeric(18,2)] | CVal | const | 0 |
| @mAllow_PLUCode | chk | const | 0 |
| @mNoOfVeh_Servs [smallint] | txtNoS | const | 0 |
| @mTelItem_Name [nvarchar(500)] | `txtTel.Text` raw | const | `''` |
| @mAllow_Serialno | chk | const | 0 |
| @mHSNCODE [varchar(50)] | `CStr1(cmbhsncode).Trim()` | **user** | free text (combo lists HSN_Master ∪ existing item HSNs; HSN_Master is empty) |
| @mCGST / @mSGST [numeric(18,2)→col (9,2)] | `CVal(cmbvat)/2` (Decimal) | derived | VAT/2 (e.g. 0.10→0.05) |
| @mIGST | CVal(cmbvat) | derived | VAT |
| @mCGSTVIP_ID, @mCGSTVOP_ID, @mSGSTVIP_ID, @mSGSTVOP_ID | combos | derived | §4 |
| @mSale_PACK [varchar(50)] | `CStr1(txtSalePack).Trim()` (MaxLength 15) | default | pre-filled from Sale_PACK of MAX(Item_ID) item; = Item_Pack in all rows |
| @mGSTCess [numeric(9,2)] | CVal(txtCessRate) | const | 0 |
| @mGSTCess_ActID / @mGSTAddlCess_ActID [int] | CmbCessAct / cmbAddlCessAct (no blank row) | derived | first row of `Select Act_name,Act_Id from IQAccmst where (Act_Type='Income' or Act_Type='Expense') and Act_Cat='A' Order by Act_name` → 11132 "Cash discount" (MBM), 10203 "ADD CESS" (MP) |
| @mGSTAddLCessRate | CVal | const | 0 |
| @mUQC [varchar(60)] | `CStr1(cmbUQC).Trim()` | default | `Configs.Default_HSNUQC` = `PCS-Pieces` |
| @mGoods_Or_Serv [tinyint] | RbService | const | 0 |
| @mUniformCode [varchar(50)] | Trim | const | `''` |
| @mRet_Disc1Per / @mRet_Disc2Per [float] | CVal→double | const | 0 |
| @mIsWgtBarcode | chk | const | 0 |
| @mEnableSetNewGSTRate_On_ItemPriceAbove_TextileItems | chk | const | 0 |
| @mNewGSTRate_TextileItems [numeric(9,2)] | txt (designer `"18"`) | const | **18.00** |
| @mItemPriceAbove_TextitleItems [numeric(18,2)] | txt (designer `"2500"`) | const | **2500.00** |
| @mIsTobaccoItem | chk | const | 0 |
| @mMaxCheckSaleDiscPer [numeric(9,2)] | txt (`"0"`) | const | 0 |

Columns not in the proc keep their table default on insert: `ThridPack_Cap='0'` (varchar default `((0))`),
`ThridPack_Value=0`, `Allow_BOM=0`, `Cost_Calculated=0`; NULL: `ItemGSTTYPE`, `Stk`, `LastPLUCode`.
`Stk` is maintained later by stock procs (NULL until first transaction).

### 3.2 `IQItems_Units_insert` (`ClsItems.cs:144`; call `FrmItems.cs:9223`)

| Param [proc type] | HQ source | Value |
|---|---|---|
| @mRecd_ID [int] | 0L | 0 |
| @mItem_ID | id | |
| @mUNit_Id, @mUnit_PrimaryID [int] | literal 3L, 3L (**hard-coded, ignores CmbPack**) | 3, 3 |
| @mUnit_Qty, @mUnit_Equ [int] | 1L, 1L | 1, 1 |
| @mUnit_Prate [money] | `CVal(txtPrate)` (Decimal) | **user: purchase rate** |
| @mUnit_Srate [money] | `CVal(txtPTR)` | 0 on recent items (non-zero only on the Dec-2023 Excel import rows) |
| @mUnit_WSrate [money] | `CVal(txtSrate)` | **user: sale rate** (the form's "Sale Rate" box lands in *WSrate*) |
| @mUnit_MRP [money] | `CVal(txtMrp)` | user (0 on every item seen) |
| @mLn_No [tinyint], @mChrg_Set [smallint] | 0, 0 | |
| @mWs_Disc, @mrs_Disc [numeric(9,2)] | 0.0, 0.0 | |
| @mPGRATE, @mPDRATE [money/smallmoney], @mSGRATE, @mSDRATE [smallmoney] | form fields (0 on create) | 0 |
| @mPLBExcTYPE [nvarchar(100)] | `byte 0` | stored as nvarchar **`'0'`** |
| all other P*/S* excise/tax/add/less/diff/type params | 0 | 0 |
| @mSP_PurRate [numeric(18,2)] | `CVal(txtSP_Prate)` → double | IssueConv=1: = purchase rate; else `Prate/IssueConv` formatted `0.0000` |
| @mPP_SaleRate [numeric(18,2)] | `CVal(txtSP_Srate)` → double | same with sale rate |
| @mReportPack [varchar(50)] | `CStr1(txtReport_Thrid_packTitle).Trim()` | `''` |
| @mIssueConv [numeric(18,2)] | CVal(txtIssueConv) (designer `"1"`; forced 1 when pack=salepack) | 1 |
| @mStockConv [tinyint] | `RbReportBasedon_SalePack ? 0 : 1` (designer: PurchasePack checked) | **1** |
| @mThirdConv | 0 | 0 |
| @mSPLRate1..10 [float] | 0 | 0 |

Rounding trap: SP_PurRate/PP_SaleRate travel as **double** and SQL rounds the binary value to 2 dp, e.g. Unit_WSrate
349.575 → PP_SaleRate **349.57** (ISTMBM26 item 2471; also 3645.725→3645.72 on 2437). Decimal half-up gives .58 — wrong.
Pass a `double` (SqlDbType.Float) exactly like HQ. Rates themselves go as Decimal → money (4 dp).

### 3.3 `IQItems_Mast_Update_ItemImages`
`@mItem_ID, @mItem_Image1..4` = `CStr1(Image_n_FileName).Trim()` → `''` when no image (all items). Only called when
`Configs.Hide_ItemImagesTAB_Items=0`.

### 3.4 `Ttype_Acnt_insert` (`ClsItems.cs:264`)
`@ic_id`=Item_ID, `@tt_id`=TTypes.tt_id, `@act_id`=§4, `@set_no`=0 (grid col 3; 0 everywhere), `@ent_date`=`mLogDate`
(DateTime, date only → `2026-11-04 00:00:00.000`, same day as Ent_Date), `@Ent_uid`=mUserId (1), `@Edmode`=0 (unused).

---

## 4. GST rate → accounts

### 4.1 Rows written to Ttype_Acnt
Grid rows = `SELECT vouchtype, tt_id, ttype, Firstor From TTypes WHERE vouchtype in ('Purchase','Sales','Purchase Return','Sales Return','Purchase Order','Sales Order','Transfer In','Transfer Out') GROUP BY vouchtype, tt_id, ttype, Firstor ORDER BY Vouchtype, tt_id`
(`Fill_GridVAT_Accounts`, `:10754`). Insert order = this order. Read it **at save time** — the set changes when TTypes are added
(older items lack 245/512 because those types came later).

ISTMBM26: Purchase 211(F1) 500(F1) 501(F0) · Purchase Order 248(F1) · Purchase Return −1(F1) 504(F1) 505(F0) ·
Sales −2(F1) 266(F1) 506(F1) 507(F0) · Sales Order 249(F1) · Sales Return 510(F1) 511(F0) · Transfer In 245(F0) · Transfer Out 512(F1).
ISTMP26: same minus 211, and Transfer Out is 252.

### 4.2 Rule (`Set_GSTAccounts`, `FrmItems.cs:12723-14417`, fired by `cmbvat_SelectedIndexChanged` when `Configs.GST>0`)
Side: **purchase** = vouchtype Purchase / Purchase Return / Purchase Order / Transfer In; **sales** = Sales / Sales Return / Sales Order / Transfer Out.

For rate r ∈ {3, 5, 12, 18, 28, 0.1, 40} (compared as `CVal(cmbvat.Text) = r`), with h = r/2 and labels `0.10%`/`0.05%` for 0.1:

| Target | Firstor 0 | Firstor 1 |
|---|---|---|
| Ttype row, purchase side | Act_Tt `IGST Purchase r%` | `GST Purchase r%` |
| Ttype row, sales side | `IGST Sales r%` | `GST Sales r%` |
| VIP_ID / VOP_ID | `IGST Input r%` / `IGST Output r%` | |
| CGSTVIP_ID / CGSTVOP_ID | `CGST Input h%` / `CGST Output h%` | |
| SGSTVIP_ID / SGSTVOP_ID | `SGST Input h%` / `SGST Output h%` | |

Any other rate (incl. **0**): all six tax ids = 0; purchase-side rows → Act_Tt `Purchase – Exempted`, sales-side →
`Sales – Exempt` (**U+2013 en dash** in both source and `IQAccmst.Act_Tt`). Transfer In/Out rows get these too
(ISTMBM26 items 2312 etc.).

Lookup mechanics:
* `GetAccountID(tt)` = LINQ on IQAccmst `Act_Tt = tt ORDER BY Act_Id DESC` → first (proven by the exempt case where
  Act_Name ≠ Act_Tt: `Purchase of Exempted A/c` / `Sales Exempted  A/C`). Not found → 0.
* Ttype rows store the **Act_Name**; at save `SetAccountID_To_Grid` runs `SELECT Act_ID FROM IQaccmst WHERE Act_Name='<name>'`
  (first row, unordered). Equivalent unless two accounts share a name.
* The six combos only accept ids present in their lists (`Fill_ItemsCreation_Comboboxs`: e.g. `IGSTInputacts` =
  IQAccmst ⋈ Sale_types on Act_Tt=Sale_text where `Sale_Text LIKE 'IGST Inp%' AND ISNULL(Sign_Type,0)=0`); otherwise → 0.

Observed (ISTMBM26, 18%): VIP 10321, VOP 10345, CGSTVIP 10395, CGSTVOP 10341, SGSTVIP 10397, SGSTVOP 10343;
Ttype: F1 purchase 10356 `GST Purchase 18%`, F0 purchase 10320, F1 sales 10364, F0 sales 10344 — 581/581 recent items match.
5%, 12%, 0% items also match the rule. ISTMP26 18%: 10125/10149/10199/10145/10201/10147, Ttype 10160/10124/10168/10148.

Decompiled quirk: in the 5% block the fallback branch (Firstor not 0/1) writes the account name into grid column 5 instead
of 1 — unreachable because Firstor is always 0/1.

---

## 5. Real-data profile (30 newest items, ISTMBM26; ISTMP26 identical except noted)

User-varying columns: **Item_Name, Item_Code (+CodeAlias), Grp_ID, HSNCODE, VAT/CGST/SGST/IGST (+ derived account ids),
Unit_Prate (+SP_PurRate), Unit_WSrate (+PP_SaleRate)**; rarely Cat_ID, Item_Desc. Everything else is constant or derived:

* Constant on all 30: values in §3.1/§3.2 tables (Rack_No/Sch_Drug `' '`, DeActReason/Item_Desc/SizeCol/TelItem_Name/
  UniformCode/Item_Image1-4/ReportPack `''`, packs 3, Inc_Tax 1, StockConv 1, IssueConv 1, PLBExcTYPE `'0'`,
  textile 18.00/2500.00, Ent_Uid 1, Set_NO 0).
* Firm-derived: Item_Pack=Sale_PACK (`KGS` / `1 PIC`), UQC `PCS-Pieces`, GSTCess_ActID=GSTAddlCess_ActID (11132 / 10203).
* NULL on insert: ItemGSTTYPE, Stk, LastPLUCode.
* ISTMP26 older rows have NULL in IsWgtBarcode / textile / IsTobaccoItem / MaxCheckSaleDiscPer (created before those
  columns existed and never re-saved); current HQ always writes 0 / 18 / 2500 / 0 / 0.

Realistic user inputs for mk-hq: name, group (Grp_ID), GST % (from Sale_Types list), HSN, purchase rate, sale rate;
optional: code override (default COUNT+1), category, description, MRP. Pack/sale pack: copy HQ's default (last item's).

---

## 6. Things that would make a foreign row look different

1. `' '` (single space) in Rack_No and Sch_Drug — not `''`, not NULL.
2. `''` (not NULL) in DeActReason, Item_Desc, SizeCol, TelItem_Name, UniformCode, Item_Image1-4, ReportPack.
3. `ThridPack_Cap='0'`, PLBExcTYPE `'0'` (nvarchar).
4. Textile defaults 18.00 / 2500.00 even when the feature is off.
5. Item_Pack is **not trimmed** by HQ (others are); names/codes trimmed. 420 untrimmed names are Dec-2023 import rows, not HQ form rows.
6. Ent_Date = login date (int yyyymmdd), Ttype_Acnt.Ent_Date = same date as datetime at midnight.
7. SP_PurRate/PP_SaleRate float→numeric rounding (§3.2).
8. Unit row always UNit_Id 3 / Unit_PrimaryID 3 / Ln_No 0 / Recd_ID 0; exactly one row per item (2485/2485).
9. One Ttype_Acnt row per current TTypes row, none with Act_ID 0 (0 such rows exist).
10. Item_Name proc param is varchar(70) (column is 300): non-CP1252 characters become `?`, >70 chars truncated.
11. Item_Ord: an empty group yields 0 (NULL→CVal 0), not 1.
12. GSTCess_ActID non-zero on new items (first Income/Expense account), preserved 0 on old items when modified.
13. Modify must regenerate the unit row (new ItemUnitsDtldID) and rewrite all Ttype rows with the new date — leaving them untouched is detectable.

---

## 7. Cross-check vs the TS prototype (`core/packages/hq-gateway/src/native-product-creation.ts`)

The prototype copied a **template item** and overrode user fields. Where it disagrees with HQ:

| Topic | Prototype | HQ (trust this) |
|---|---|---|
| Non-user columns | copied from template row | form defaults (§3.1); e.g. template-copied GSTCess_ActID 0 vs HQ first Income/Expense a/c |
| Ttype mapping | reuses template's stored rows | always rate-derived for a new item |
| 0% Transfer In/Out rows | act_id 0 when template lacks them | exempt accounts |
| Supported rates | 0,5,12,18,28 | also 3, 0.1, 40 |
| SP_PurRate/PP_SaleRate | Decimal ROUND_HALF_UP | double → SQL numeric (349.575→349.57) |
| Ent_Date | wall clock | HQ login date |
| Item_Ord | `ISNULL(MAX(Item_Ord),-1)+1` | `MAX(ISNULL(Item_Ord,0))+1`, NULL→0 (differs only if group has only NULL ords) |
| Alias check | always, `LTRIM(RTRIM(CodeAlias))` | only if ShowCodeAlias>0, `ltrim(isnull(..))` |
| Name check | exact name in group | same but apostrophes stripped from probe (prototype is stricter — fine) |
| Transaction | one tran + TABLOCKX | none (we should keep a tran; harmless) |

Agreements: proc names/param lists, Rack/Sch `' '`, images `''`, textile 18/2500, PLBExcTYPE string, unit 3/3, COUNT+1 code, Act_Tt lookup by `Act_Id DESC` then resolve by Act_Name.

Engine note: `Catalogue.cs:77` reads sale rate as `COALESCE(PP_SaleRate, Unit_Srate)`; HQ's sale rate is `Unit_WSrate`
(`Unit_Srate` is the hidden PTR field, 0 on new items). Equivalent while PP_SaleRate is non-NULL.

---

## 8. Open questions

1. Meaning of the 1–2 digit suffix staff append to the COUNT+1 code (user habit vs. something HQ does on keypress).
2. Does HQ store opening stock for an item anywhere `Items_delete` doesn't check? (Not written by this screen; deletion check covers IQDoc_REcd.)
3. `GetAccountID`'s LINQ predicate is not visible in the decompile; Act_Tt equality is inferred from data (exempt accounts). Exact trimming/casing unknown.
4. `CmbPack` new-item default (`Text="Sheets"`) vs `@mDefUnitID` — data says all four pack ids are 3; the code path that guarantees 3 for Pur/Sal/Rpt packs was not fully traced.
5. ISTMP26 `UpdateRecent_Pur_Rate_Disc_To_ItemMast` is NULL; purchase-entry rate write-backs (`UpdateUnit_Rates`, `UpdateRecent_PurchaeRate_DiscPer_To_ItemsMaster` in FrmInwards/FRMPOS) also touch item rows and are out of scope here.
