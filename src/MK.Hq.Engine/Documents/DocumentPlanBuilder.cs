using System.Globalization;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Plans;

namespace MK.Hq.Engine.Documents;

/// <summary>Small helpers to write HQ procedure argument lists compactly.</summary>
public sealed class Args : Dictionary<string, object?>
{
    public Args() : base(StringComparer.OrdinalIgnoreCase) { }

    public Args Zero(string names) { foreach (var n in Split(names)) this[n] = 0; return this; }
    public Args Blank(string names) { foreach (var n in Split(names)) this[n] = ""; return this; }
    public Args Space(string names) { foreach (var n in Split(names)) this[n] = " "; return this; }
    public Args Set(string name, object? value) { this[name] = value; return this; }

    private static string[] Split(string names) => names.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Turns a <see cref="ResolvedDocument"/> into the exact sequence of HQ procedure calls HQ's own bill form
/// (FrmInwards.CmdSave_Click) performs — but as one atomic plan.
/// </summary>
public static class DocumentPlanBuilder
{
    public const string DocId = "docId", Number = "number";

    public static WritePlan Create(ResolvedDocument d, int hqUserId, string machine)
    {
        var steps = new List<PlanStep>();
        var t = d.Type;
        var docId = new Ref(DocId);
        var number = new Ref(Number);

        steps.Add(new AllocateNumberStep("Allocate the next bill number", t.TtId, t.Series, d.Suffix, Number));
        steps.Add(new CallStep("Bill header", "IQDoc_Header_Insert", Header(d, number, hqUserId, machine, modifyCount: 2), new Dictionary<string, string> { ["mDoc_Id"] = DocId }));
        steps.Add(new AssertNumberFreeStep("HQ duplicate-number rule", t.TtId, t.Series, number, d.Suffix, docId, t.SeriesType == 3));
        steps.AddRange(Body(d, docId, number, hqUserId));
        steps.AddRange(Audit(d, docId, revision: 1));
        steps.AddRange(Verification(d, docId));
        return new WritePlan($"{t.Name} {t.Series}", steps) { Locks = [] };
    }

    /// <summary>Lines, party snapshot, charges, VAT summary, ledger and stock, after the header exists.</summary>
    public static IEnumerable<PlanStep> Body(ResolvedDocument d, object docId, object number, int hqUserId, IReadOnlyList<long?>? existingLineIds = null)
    {
        for (var i = 0; i < d.Items.Count; i++)
            yield return LineStep(d, i, docId, d.OrderLineIds.ElementAtOrDefault(i), existingLineIds?.ElementAtOrDefault(i));
        yield return CashTranStep(d, docId);
        for (var i = 0; i < d.Charges.Count; i++)
        {
            var charge = d.Charges[i];
            var amount = d.RoundingCharge?.ChargeId == charge.ChargeId ? d.Quote.RoundOff : 0m;
            yield return new CallStep($"Charge row {charge.Name}", "IQDoc_Chrgs_Insert", new Args()
                .Set("mDoc_id", docId).Set("mLn_No", i + 1).Set("mChrg_id", charge.ChargeId).Set("mCIndx", i + 1)
                .Set("mRate", amount).Set("mAmount", amount).Set("mAct_ID", charge.AccountId).Set("mChrg_Desc", "")
                // HQ's legacy VAT total is zero for the first charge row; later zero-rate rows carry the item net.
                .Set("mCalcOnAmt", i == 0 ? 0m : d.Quote.BeforeRounding).Set("mTax5Amt", 0).Set("mTax145Amt", 0));
        }
        // CmdSave_Click writes this legacy zero VAT summary for every bill type, orders included.
        yield return new CallStep("Legacy VAT summary", "IQDOC_VAT_Insert", new Args().Set("mDoc_Id", docId)
            .Zero("mExempted mGross4 mTax4 mGross145 mTax145 mGross1 mTax1 mTaxSpec mCashDisc mFrieght mCrDr_Note mRndOff"));

        if (d.Kind.IsOrder()) yield break;
        foreach (var step in LedgerSteps(d, docId, number, hqUserId)) yield return step;
        foreach (var step in StockSteps(d.Items.Select(m => m.Item.ItemId))) yield return step;
    }

    public static IEnumerable<PlanStep> LedgerSteps(ResolvedDocument d, object docId, object number, int hqUserId)
    {
        foreach (var row in d.Ledger)
        {
            yield return new CallStep($"Ledger {row.Type} {row.AccountId}", "IQdaybook_insert", new Args()
                .Set("trdate", d.Request.Date).Set("Act_id", row.AccountId).Set("Amount", row.Amount).Set("type", row.Type.ToString())
                .Set("Contra_id", row.ContraId)
                .Zero("BL_Adjusted Adjusted ARS ARS_Ord Div_no days_from days_to int_credit discount doc_type adj_id")
                .Set("Doc_ID", docId).Set("VType", "0").Set("Tseries", d.Type.Series).Set("Tno", number)
                // SaveDaybookActwise_Summary stores no suffix in the ledger.
                .Set("Textn", "")
                .Set("Narration", Narration(d, number, row.AccountId == d.Head.AccountId && d.Settlement != Settlement.Cash))
                // HQ passes Conversions.ToInteger(total qty): banker's rounding to a whole number (76.5 → 76).
                .Set("Bill_Qty", Math.Round(d.Quote.Quantity, 0, MidpointRounding.ToEven)).Set("ent_Uid", hqUserId)
                // Before HQ's 3-Nov-2026 update this was NULL; the installed build stores 0.
                .Set("Salesman_ID", 0));
        }
    }

    /// <summary>HQ's own save refreshes derived stock: clear the cost flag and recompute IQItems_Mast.Stk.</summary>
    public static IEnumerable<PlanStep> StockSteps(IEnumerable<long> itemIds, bool clearEmpty = false)
    {
        foreach (var id in itemIds.Distinct().Order())
        {
            yield return new SqlStep($"Cost flag item {id}", "UPDATE dbo.IQItems_Mast SET Cost_Calculated=0 WHERE Item_ID=@id", new Args().Set("id", id));
            yield return new CallStep($"Stock refresh item {id}", "UpdateClosing_Stock_B+NG", new Args().Set("mItem_ID", id)) { ReadCommitted = true };
            if (clearEmpty)
                // HQ's refresh skips an item whose last movement was removed; clear that stale figure explicitly.
                yield return new SqlStep($"Clear empty stock item {id}", """
                    UPDATE dbo.IQItems_Mast SET Stk=0 WHERE Item_ID=@id
                      AND NOT EXISTS (SELECT 1 FROM dbo.IQDoc_Recd r JOIN dbo.IQDoc_Header h ON h.Doc_Id=r.Doc_ID WHERE r.Item_ID=@id AND ISNULL(r.DcRecd_ID,0)=0)
                      AND NOT EXISTS (SELECT 1 FROM dbo.IQDoc_Delv x JOIN dbo.IQDoc_Header h ON h.Doc_Id=x.Doc_ID WHERE x.Item_ID=@id AND ISNULL(x.DcDelv_ID,0)=0)
                    """, new Args().Set("id", id));
        }
    }

    public static Args Header(ResolvedDocument d, object number, int hqUserId, string machine, int modifyCount)
    {
        var q = d.Quote;
        var a = new Args()
            .Zero("mBranch_ID mSM_Id mDc_Amt mDb_Save mTcBook mDoc_Cancel mCash_Discount mDoc_Type mZDoc_Id mGunn_Calc mBill_Cost mETno mEst_Amt mRecd_Amt mRended_Amt mPack mAmcNo mWayBillNo mDiscValue mAdjValue mDoctorID mFromSP_ID")
            .Blank("mTExtn mDc_no mFStype mEtseries mEtExtn mAmcSeries mWayBillSeries mComments")
            .Blank("""
                mMessage mDoctorname mSex mPARTYDISCID mAutoMob_RegdNo mAutoMob_EngineNo mAutoMob_FrameNo mAutoMob_ModelNo
                mAutoMob_Color mAutoMob_Other1 mAutoMob_Other2 mAutoMob_KeyNo mShip_ContactPerson mShip_AddLine1 mShip_AddLine2 mShip_Town
                mShip_District mShip_state mShip_PinNo mShip_Ph1 mShip_Ph2 mShip_STD mShip_Cell mShip_Email mShip_GSIN mShip_PANNO mMobNo
                mVehicle_No meWaybill_TransName meWaybill_TransID meWaybill_TransVehicle_Doc_No mBook_Series mParam_DCNo mParam_OrdNo
                mParam_TransportName mParam_VehicleNo mParam_Cases
                """)
            .Set("mDoc_Date", d.Request.Date).Set("mDoc_Duedate", d.DueDate)
            .Set("mTt_Id", d.Type.TtId).Set("mTSeries", d.Type.Series).Set("mTNo", number).Set("mTExtn", d.Suffix)
            .Set("mAct_ID", d.ActId).Set("mHeadAct_ID", d.Head.AccountId).Set("mSp_Id", d.Type.StockPointId)
            .Set("mBill_Amt", q.Payable).Set("mMode", d.Mode)
            .Set("mSupp_Billno", d.Request.Supplier?.Number.Trim() ?? "")
            // Outside purchases the form's supplier-date picker keeps its default: the day the bill is entered.
            .Set("mSupp_Billdate", d.Request.Supplier?.Date is { } supplierDate ? supplierDate : new Clock(ClockField.Day))
            .Set("mSupp_BillAmt", q.Payable)
            .Set("mBill_Time", new Clock(ClockField.Time)).Set("mEnt_Time", new Clock(ClockField.Time))
            .Set("mEnt_Date", new Clock(ClockField.Day)).Set("mVisitDate", new Clock(ClockField.Day))
            .Set("mEnt_Uid", hqUserId).Set("mModify_By", hqUserId).Set("mModify_Date", new Clock(ClockField.Stamp))
            .Set("mNarration", d.Notes).Set("mBill_Qty", q.Quantity).Set("mWayBillDate", d.Request.Date)
            .Set("mExclusive", q.Policy.Basis == TaxBasis.Inclusive ? 1 : 0).Set("mGST_Type", (int)d.Type.TaxMode)
            .Set("mProduct_Value", q.BeforeRounding).Set("mRndOff", 0).Set("mDH_VouchType", d.Type.VouchType)
            .Set("mBillEnteredSystem", machine).Set("mModifyCnt", modifyCount)
            .Set("mSM_Id", d.Salesperson?.Id ?? 0).Set("mDoctorID", d.Salesperson?.Id ?? 0).Set("mDoctorname", d.Salesperson?.Name ?? "")
            .Set("mEtseries", " ").Set("mComments", " ")
            .Set("mMobNo", DocumentResolver.Text(d.Address, "Act_Cell"))
            .Set("mMessage", d.Message);
        // HQ copies the selected account's ship-to block on every bill, cash included (walk-in Cash has none).
        {
            foreach (var (param, column) in new[]
                     {
                         ("mShip_ContactPerson", "ship_Person"), ("mShip_AddLine1", "ship_Add_Line1"), ("mShip_AddLine2", "ship_Add_Line2"),
                         ("mShip_Town", "ship_Act_Town"), ("mShip_District", "ship_Act_District"), ("mShip_state", "ship_Act_state"),
                         ("mShip_PinNo", "ship_Act_PinNo"), ("mShip_Ph1", "ship_Act_Ph1"), ("mShip_Cell", "ship_Act_Cell"),
                         ("mShip_Email", "ship_Act_Email"), ("mShip_GSIN", "ship_Act_GSIN"), ("mShip_PANNO", "ship_Act_PANNO"),
                     })
                a[param] = DocumentResolver.Text(d.Address, column);
            // The form's ship-to state defaults to the firm's state when the account has none.
            if (d.Kind is DocumentKind.Sale or DocumentKind.SalesOrder && string.IsNullOrEmpty((string?)a["mShip_state"])) a["mShip_state"] = d.Profile.StateCaption;
        }
        return a;
    }

    /// <summary>One bill line. With <paramref name="existingLineId"/> HQ's procedure updates that line in place (HQ's edit path).</summary>
    public static CallStep LineStep(ResolvedDocument d, int index, object docId, long orderLineId = 0, long? existingLineId = null)
    {
        var m = d.Items[index];
        var q = d.Quote.Lines[index];
        var interstate = d.Type.TaxMode == TaxMode.Interstate;
        var description = d.Request.Lines[index].Description ?? m.ItemDescription;
        // Inclusive IGST keeps two rounded legacy half-tax columns; TaxValue and the ledger keep the single IGST.
        decimal? legacyHalf = interstate && d.Quote.Policy.Basis == TaxBasis.Inclusive
            ? Money.Round2((q.Quantity * q.Rate - q.Discount) * q.IgstRate / (q.IgstRate + 100) / 2)
            : null;
        var common = new Args()
            .Set("mDoc_ID", docId).Set("mLn_No", index + 1).Set("mItem_ID", m.Item.ItemId).Set("mUnitID", m.Item.UnitId)
            .Set("mQty", q.Quantity).Set("mRate", q.Rate).Set("mFree", 0).Set("mDisc", q.DiscountPercent).Set("mDiscValue", q.Discount)
            .Set("mValue", q.Gross).Set("mGrossValue", q.Taxable).Set("mNetValue", q.Net)
            .Set("mAct_Id", m.BaseAccountId).Set("mTax_Id", m.TaxAccountId)
            .Set("mTax", q.TaxRate).Set("mTaxValue", q.AggregateTax)
            // Interstate lines still carry HQ's legacy CGST/SGST columns: half the IGST rate each.
            .Set("mCGST_Tax", interstate ? q.IgstRate / 2 : q.CgstRate).Set("mCGST_TaxAmt", legacyHalf ?? (interstate ? q.Igst : q.Cgst))
            .Set("mSGST_Tax", interstate ? q.IgstRate / 2 : q.SgstRate).Set("mSGST_TaxAmt", legacyHalf ?? q.Sgst).Set("mSGST_TaxID", m.SgstAccountId)
            .Set("mGST_Type", (int)d.Type.TaxMode).Set("mHSNCODE", m.Item.Hsn)
            .Set("mItemDesc", string.IsNullOrEmpty(description) ? " " : description)
            .Set("mStockconv", m.StockConversion).Set("mPackConv", m.IssueConversion).Set("mThirdPackConv", m.ThirdConversion)
            .Set("mSize", " ").Set("mCess_ActID", m.CessAccountId).Set("mAddLCess_ActID", m.AdditionalCessAccountId);

        if (d.Kind.IsOrder())
        {
            common.Remove("mSGST_TaxID");
            common.Set("mSGST_TaxID", m.SgstAccountId).Set("mUnitEqu", m.UnitEquivalence).Set("mItemDesc", description)
                .Blank("mOthers1 mOthers2 mOthers3");
            if (existingLineId is { } ordLine)
                // Edit_Data reopens order rows with their own id and without the hidden pack/cess fields.
                common.Set("mOrdDltdID", ordLine).Set("mRecdOrDelv_ID", ordLine).Zero("mStockconv mPackConv mThirdPackConv mCess_ActID mAddLCess_ActID");
            return new CallStep($"Order line {index + 1}", "IQOrd_Detail_insert", common, new Dictionary<string, string> { ["mOrdDltdID"] = $"line{index}" });
        }

        common.Zero("""
            mPCS mKGS mPackTotal mSchQty mEstQty mEstFree mMargin mCD mCDAmt mExcise mExciseValue
            mTempQty mTempFree mBalQty mBalFree mEBalQty mEBalFree mReplacement mSupplied mDataType mTemp_NumFld1 mTemp_NumFld2 mTemp_NumFld3
            mTemp_IntFld1 mTemp_IntFld2 mTemp_IntFld3 mMultiMrp
            """)
            .Set("mSM_ID", d.Salesperson?.Id ?? 0).Set("mMRP", m.Mrp).Set("mSRate", m.Item.SaleRate)
            .Set("mEValue", 0).Set("mEGrossValue", 0).Set("mENetValue", 0).Set("mSp_ID", d.Type.StockPointId)
            .Set("mUNitequ", m.UnitEquivalence).Set("mUNitPRimaryID", m.PrimaryUnitId).Set("mInc_Tax", m.ItemInclusive)
            .Set("mItemSerialnos", "").Blank("mPLUCode mNos mTimberDetailStrs mOthers1 mOthers2 mOthers3 mOriginal_InvNo")
            .Set("mTimberType", "Size").Set("mCostCode", " ")
            // Disabled textile settings still travel from the item master to the line, as on HQ's form.
            .Set("mNewGSTRateTextileItems", m.TextileRate).Set("mItemPriceAboveTextitleItems", m.TextileThreshold);

        if (d.Kind == DocumentKind.Sale)
        {
            common.Zero("mRecd_ID mDCDelv_Id mIndentDelv_ID mTempDelv_ID").Set("mOrdDelv_Id", orderLineId).Set("mTaxOldRate", m.OldTax)
                .Zero("mPTR mPTS mPTD");
            if (existingLineId is { } delv) common.Set("mDelv_Id", delv);
            return new CallStep($"Sale line {index + 1}", "IQDoc_Delv_insert", common, new Dictionary<string, string> { ["mDelv_Id"] = $"line{index}" });
        }

        common.Set("mBatchno", " ").Set("mSehme", " ").Set("mPLUNO", "")
            .Zero("""
                mDCRecd_Id mOrdRecd_Id mIndentRecd_ID mTempRecd_ID mPGRATE mPLBExcise mPLBExcTYPE mPDRATE mPDTYPE mPEXCISE mPETYPE mPCTAX mPCTYPE
                mPSTAX mPSTYPE mPADD mPLESS mPDIFF mSGRATE mSLBExcise mSLBExcType mSLDiscount mSLDisType mSDRATE mSDTYPE mSEXCISE mSETYPE
                mSCTAX mSCTYPE mSSTAX mSSTYPE mSADD mSLESS mSDIFF
                """)
            .Set("mMfgDate", new Clock(ClockField.CompactDay)).Set("mExpDate", new Clock(ClockField.CompactDay)).Set("mSchDate", new Clock(ClockField.CompactDay))
            .Set("mPTR", Money.Round2(m.DealerRate)).Set("mPTS", Money.Round2(m.WholesaleRate)).Set("mPTD", Money.Round2(m.DealerRate))
            .Set("mPSTAX", m.OldTax).Set("mSGRATE", m.SgRate)
            // HQ fills this only when the margin column is typed into (7% of its rows); otherwise 0.
            .Set("mSaleRateMarginEntryPer", 0);
        if (existingLineId is { } recd) common.Set("mRecd_Id", recd);
        return new CallStep($"Purchase line {index + 1}", "IQDoc_Recd_insert", common, new Dictionary<string, string> { ["mRecd_Id"] = $"line{index}" });
    }

    public static CallStep CashTranStep(ResolvedDocument d, object docId)
    {
        var a = d.Address;
        string T(string key) => DocumentResolver.Text(a, key);
        string OrSpace(string v) => string.IsNullOrEmpty(v) ? " " : v;
        var p = d.Snapshot;
        if (d.Settlement == Settlement.Bank)
        {
            // In bank modes HQ stores the payer's details (holder) in the snapshot, with no account id.
            return new CallStep("Party snapshot (bank holder)", "IQCash_TRAN_insert", new Args()
                .Set("mDoc_Id", docId).Set("mAct_ID", 0).Set("mAct_Name", d.Request.Party?.Name ?? "")
                .Set("mAddress", OrSpace(d.Request.Party?.Address ?? "")).Set("mAddress2", "")
                .Set("mAct_Town", OrSpace(d.Request.Party?.Town ?? "")).Set("mAct_TINNO", "").Set("mAct_SSTNO", "").Set("mAct_CSTNO", "")
                .Set("mAct_PH1", OrSpace(d.Request.Party?.Phone ?? T("Act_Cell"))).Set("mAct_Mob", "")
                .Set("mAct_Dl20B", " ").Set("mAct_DL21B", " ").Set("mAct_Code", "")
                .Set("mAadharNo", T("AadharNo")).Set("mPANNO", T("Act_PAN")).Set("mGSTIN", T("GSTIN"))
                .Set("mGSTTaxType", (int)(DocumentResolver.Number(a, "GSTTaxType") ?? 2)).Set("mActstate", T("Act_state")).Set("mPincode", T("Act_PinNo")));
        }
        var cashWalkIn = d.Settlement == Settlement.Cash;
        return new CallStep("Party snapshot", "IQCash_TRAN_insert", new Args()
            .Set("mDoc_Id", docId).Set("mAct_ID", d.Head.AccountId).Set("mAct_Name", p.Name)
            .Set("mAddress", OrSpace(cashWalkIn && d.Request.Party?.Address is { } addr ? addr.Trim() : T("Add_Line1")))
            .Set("mAddress2", OrSpace(T("Add_Line2")))
            .Set("mAct_Town", OrSpace(p.Town)).Set("mAct_TINNO", OrSpace(T("Act_VAT_TINNO"))).Set("mAct_SSTNO", OrSpace(T("Act_SSTNo")))
            // Save_CashTran clears this obsolete field for GST bills.
            .Set("mAct_CSTNO", "")
            .Set("mAct_PH1", OrSpace(p.Phone)).Set("mAct_Mob", d.Request.Party?.Mobile?.Trim() ?? T("Act_Cell"))
            .Set("mAct_Dl20B", OrSpace(T("Act_DL20B"))).Set("mAct_DL21B", OrSpace(T("Act_DL21B")))
            // A walk-in cash customer has no account code on HQ's form.
            .Set("mAct_Code", d.Head.AccountId == InvoiceLedger.HqCashAccountId ? " " : d.Head.Code)
            .Set("mAadharNo", T("AadharNo")).Set("mPANNO", T("Act_PAN")).Set("mGSTIN", p.Gstin).Set("mGSTTaxType", p.GstType)
            .Set("mActstate", p.State).Set("mPincode", T("Act_PinNo")));
    }

    /// <summary>HQ's DocNarration / ContNarration from AccountPosting_GST.</summary>
    public static object Narration(ResolvedDocument d, object number, bool partyRow)
    {
        if (d.Notes.Length > 0) return d.Notes;
        var tt = d.Type.Name;
        string reference;
        if (d.Kind == DocumentKind.Purchase && d.Request.Supplier is { } s)
            reference = $" Supp. Bill No. {s.Number.Trim()} Dt. {s.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
        else
            reference = $" Bill No {d.Type.Series}/{(number is Ref r ? "{" + r.Name + "}" : Convert.ToString(number, CultureInfo.InvariantCulture))}";
        if (partyRow) return new Interp(Escape(tt) + EscapeKeepingVars(reference));
        var town = d.Settlement == Settlement.Bank ? d.AccountTown : d.Snapshot.Town;
        var name = d.Settlement == Settlement.Bank ? d.Head.Name : d.Snapshot.Name;
        var text = Escape(tt) + (d.Kind.IsInward() ? " From " : " To ") + Escape(name) + (town.Length > 0 ? ", " : " ") + Escape(town) + EscapeKeepingVars(reference);
        return new Interp(text);
    }

    private static string Escape(string s) => s.Replace("{", "(").Replace("}", ")");
    private static string EscapeKeepingVars(string s) => s;

    public static IEnumerable<PlanStep> Audit(ResolvedDocument d, object docId, int revision, IReadOnlyList<long?>? reopenedLineIds = null)
    {
        if (!d.Profile.AuditEnabled) yield break;
        var headerColumns = """
            Doc_Id Doc_Date Doc_Duedate Branch_ID Tt_Id TSeries TNo TExtn Act_ID SM_Id Sp_Id Dc_no Dc_Amt Bill_Amt Mode
            Supp_Billno Supp_Billdate Supp_BillAmt FStype Db_Save TcBook Doc_Cancel Cash_Discount Doc_Type ZDoc_Id Bill_Time Ent_Date Ent_Uid Narration
            Gunn_Calc Bill_Qty VisitDate Bill_Cost Etseries ETno Exclusive EtExtn Est_Amt Recd_Amt Rended_Amt Pack AmcSeries AmcNo
            WayBillSeries WayBillNo WayBillDate DiscValue AdjValue DoctorID Comments FromSP_ID HeadAct_ID Act_ControlID Product_Value
            """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var docArgs = new Args().Set("id", docId);
        yield return new CopyRowsStep("Audit: header", "User_IQDoc_Header_Insert", "SELECT * FROM dbo.IQDoc_Header WHERE Doc_Id=@id", docArgs, headerColumns,
            new Args()
                // HQ's tracking copy follows the form, not the stored row, for these fields.
                .Set("mDoc_Duedate", d.Request.Date).Set("mAct_ID", d.Head.AccountId).Set("mBill_Qty", 0)
                .Set("mEtseries", " ").Set("mComments", " ").Set("mBill_Time", new Clock(ClockField.Time))
                .Blank("mMessage mSex").Set("mTExtn", "").Set("mDoctorname", d.Salesperson?.Name ?? "")
                .Zero("mSTypeID mAge mTaskType").Set("mModifyCnt", revision)
                // The tracking copy stores the form's trimmed text.
                .Set("mActName", (d.Settlement == Settlement.Bank ? d.Head.Name : d.Snapshot.Name).Trim())
                .Set("mActTown", (d.Settlement == Settlement.Bank ? d.AccountTown : d.Snapshot.Town).Trim())
                .Set("mCID", d.Profile.Firm.Database));

        if (d.Settlement == Settlement.Cash)
            yield return new CopyRowsStep("Audit: cash party", "User_IQCash_TRAN_insert",
                """
                SELECT Doc_Id, Act_ID, Act_Name, COALESCE(NULLIF(Address,''),' ') Address, COALESCE(NULLIF(Act_Town,''),' ') Act_Town,
                  COALESCE(NULLIF(Act_TINNO,''),' ') Act_TINNO, COALESCE(NULLIF(Act_SSTNO,''),' ') Act_SSTNO,
                  COALESCE(NULLIF(Act_DL20B,''),' ') Act_DL20B, COALESCE(NULLIF(Act_DL21B,''),' ') Act_DL21B, ISNULL(Act_Code,'') Act_Code,
                  COALESCE(NULLIF(Pincode,''),' ') Act_CSTNO, COALESCE(NULLIF(Act_Mob,''),' ') Act_PH1
                FROM dbo.IQCash_TRAN WHERE Doc_Id=@id
                """, docArgs,
                ["Doc_Id", "Act_ID", "Act_Name", "Address", "Act_Town", "Act_TINNO", "Act_SSTNO", "Act_DL20B", "Act_DL21B", "Act_Code", "Act_CSTNO", "Act_PH1"],
                new Args());

        yield return new CallStep("Audit: VAT summary", "User_IQDOC_VAT_Insert", new Args().Set("mDoc_Id", docId)
            .Zero("mExempted mGross4 mTax4 mGross145 mTax145 mGross1 mTax1 mTaxSpec mCashDisc mFrieght mCrDr_Note mRndOff"));

        var lineColumns = """
            Doc_Id Ln_No Item_ID UnitID MRP Size PCS KGS PackTotal Qty Free SchQty EstQty EstFree Rate SRate Value EValue Disc DiscValue
            GrossValue EGrossValue Tax TaxValue NetValue ENetValue Margin CD CDAmt PTR PTS PTD Excise ExciseValue TempQty TempFree BalQty BalFree EBalQty EBalFree
            Replacement Supplied Act_Id Tax_Id Sp_ID SM_ID DataType UNitequ UNitPRimaryID Inc_Tax MultiMrp ItemDesc ItemSerialnos
            Temp_NumFld1 Temp_NumFld2 Temp_NumFld3 Temp_IntFld1 Temp_IntFld2 Temp_IntFld3
            """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var common = new Args().Zero("mEValue mEGrossValue mENetValue").Set("mModifyCnt", revision).Set("mDcType", 0).Set("mIsReturn", 0);

        for (var i = 0; i < d.Items.Count; i++)
        {
            var m = d.Items[i];
            var q = d.Quote.Lines[i];
            var lineArgs = new Args().Set("id", docId).Set("ln", i + 1);
            if (d.Kind.IsOrder())
            {
                var values = new Args().Zero(string.Join(' ', lineColumns.Select(c => "m" + c)))
                    .Set("mSize", " ").Set("mItemDesc", string.IsNullOrEmpty(d.Request.Lines[i].Description) ? " " : d.Request.Lines[i].Description)
                    .Set("mItemSerialnos", "").Set("mDoc_Id", docId).Set("mLn_No", i + 1).Set("mItem_ID", m.Item.ItemId).Set("mUnitID", m.Item.UnitId)
                    .Set("mInc_Tax", m.ItemInclusive).Set("mMRP", m.Mrp).Set("mQty", q.Quantity).Set("mRate", q.Rate).Set("mSRate", m.WholesaleRate)
                    .Set("mValue", q.Gross).Set("mDisc", q.DiscountPercent).Set("mDiscValue", q.Discount).Set("mGrossValue", q.Taxable)
                    .Set("mTax", q.TaxRate).Set("mTaxValue", q.AggregateTax).Set("mNetValue", q.Net).Set("mAct_Id", m.BaseAccountId)
                    .Set("mTax_Id", m.TaxAccountId).Set("mSp_ID", d.Type.StockPointId).Set("mUNitequ", m.UnitEquivalence).Set("mUNitPRimaryID", m.PrimaryUnitId)
                    .Set("mPTR", Money.Round2(m.DealerRate)).Set("mPTS", Money.Round2(m.WholesaleRate)).Set("mPTD", Money.Round2(m.DealerRate));
                foreach (var (k, v) in common) values[k] = v;
                values.Set("mBatchno", " ").Set("mSehme", " ")
                    .Zero("""
                        mRecd_Id mTradeType mSaleType mDCRecd_Id mOrdRecd_Id mIndentRecd_ID mTempRecd_ID
                        mPGRATE mPLBExcise mPLBExcTYPE mPDRATE mPDTYPE mPEXCISE mPETYPE mPCTAX mPCTYPE mPSTYPE mPADD mPLESS mPDIFF
                        mSLBExcise mSLBExcType mSLDiscount mSLDisType mSDRATE mSDTYPE mSEXCISE mSETYPE mSCTAX mSCTYPE mSSTAX mSSTYPE mSADD mSLESS mSDIFF
                        """)
                    .Set("mMfgDate", new Clock(ClockField.CompactDay)).Set("mExpDate", new Clock(ClockField.CompactDay)).Set("mSchDate", new Clock(ClockField.CompactDay))
                    .Set("mPSTAX", m.OldTax).Set("mSGRATE", m.SgRate);
                yield return new CallStep($"Audit: order line {i + 1}", "User_IQDoc_Recd_insert", values);
                continue;
            }

            var reopened = reopenedLineIds?.ElementAtOrDefault(i);
            if (d.Kind == DocumentKind.Sale)
            {
                var overrides = new Args().Set("mDelv_Id", reopened ?? 0);
                foreach (var (k, v) in common) overrides[k] = v;
                // HQ's tracking copy uses the retail master rate, while the stored line carries the issue price.
                if (reopened is null) overrides.Set("mSRate", Money.Round2(m.WholesaleRate));
                yield return new CopyRowsStep($"Audit: sale line {i + 1}", "User_IQDoc_Delv_insert",
                    "SELECT * FROM dbo.IQDoc_Delv WHERE Doc_Id=@id AND Ln_No=@ln", lineArgs,
                    [.. lineColumns, "Recd_ID", "DCDelv_Id", "OrdDelv_Id", "IndentDelv_ID", "TempDelv_ID", "NetRateCalc", "Rpt_Sel"], overrides);
            }
            else
            {
                var overrides = new Args().Set("mBatchno", " ").Set("mSehme", " ")
                    .Zero("""
                        mRecd_Id mTradeType mSaleType mDCRecd_Id mOrdRecd_Id mIndentRecd_ID mTempRecd_ID
                        mPGRATE mPLBExcise mPLBExcTYPE mPDRATE mPDTYPE mPEXCISE mPETYPE mPCTAX mPCTYPE mPSTYPE mPADD mPLESS mPDIFF
                        mSLBExcise mSLBExcType mSLDiscount mSLDisType mSDRATE mSDTYPE mSEXCISE mSETYPE mSCTAX mSCTYPE mSSTAX mSSTYPE mSADD mSLESS mSDIFF
                        """)
                    .Set("mMfgDate", new Clock(ClockField.CompactDay)).Set("mExpDate", new Clock(ClockField.CompactDay)).Set("mSchDate", new Clock(ClockField.CompactDay))
                    .Set("mPSTAX", m.OldTax).Set("mSGRATE", m.SgRate);
                foreach (var (k, v) in common) overrides[k] = v;
                yield return new CopyRowsStep($"Audit: purchase line {i + 1}", "User_IQDoc_Recd_insert",
                    "SELECT * FROM dbo.IQDoc_Recd WHERE Doc_Id=@id AND Ln_No=@ln", lineArgs, lineColumns, overrides);
            }
        }

        if (d.Charges.Count > 0)
            yield return new CopyRowsStep("Audit: charges", "User_IQDoc_Chrgs_Insert", "SELECT * FROM dbo.IQDoc_Chrgs WHERE Doc_Id=@id ORDER BY Ln_No", docArgs,
                ["Doc_id", "Ln_No", "Chrg_id", "CIndx", "Rate", "Amount", "Act_ID", "Chrg_Desc"], new Args());
    }

    /// <summary>Read-back checks inside the transaction: nothing commits unless HQ stored exactly what was planned.</summary>
    public static IEnumerable<PlanStep> Verification(ResolvedDocument d, object docId)
    {
        var id = new Args().Set("id", docId);
        yield return new VerifyStep("Verify bill amount", "SELECT CAST(Bill_Amt AS decimal(19,2)) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", id, d.Quote.Payable,
            "HQ_SAVE_MISMATCH", "HQ stored a different bill amount");
        var table = d.Kind.IsOrder() ? "IQOrd_Detail" : d.Kind == DocumentKind.Sale ? "IQDoc_Delv" : "IQDoc_Recd";
        yield return new VerifyStep("Verify lines", $"SELECT COUNT(*) FROM dbo.{table} WHERE Doc_ID=@id", id, d.Items.Count, "HQ_SAVE_MISMATCH", "HQ stored a different number of lines");
        yield return new VerifyStep("Verify line total", $"SELECT CAST(ISNULL(SUM(NetValue),0) AS decimal(19,2)) FROM dbo.{table} WHERE Doc_ID=@id", id,
            d.Quote.BeforeRounding, "HQ_SAVE_MISMATCH", "HQ stored different line values");
        if (!d.Kind.IsOrder())
        {
            yield return new VerifyStep("Verify ledger rows", "SELECT COUNT(*) FROM dbo.IQDayBook WHERE Doc_ID=@id", id, d.Ledger.Count, "HQ_SAVE_MISMATCH", "HQ stored a different ledger");
            yield return new VerifyStep("Verify ledger total", "SELECT CAST(ISNULL(SUM(CASE WHEN TYPE IN ('D','P') THEN AMOUNT ELSE -AMOUNT END),0) AS decimal(19,2)) FROM dbo.IQDayBook WHERE Doc_ID=@id",
                id, d.Ledger.Sum(l => l.Signed), "HQ_SAVE_MISMATCH", "HQ stored a different ledger total");
        }
        yield return new VerifyStep("Verify party name kept", "SELECT LTRIM(RTRIM(ISNULL(Act_Name,''))) FROM dbo.IQCash_TRAN WHERE Doc_Id=@id", id,
            d.Settlement == Settlement.Bank ? (d.Request.Party?.Name?.Trim() ?? "") : d.Snapshot.Name, "HQ_FIELD_MISMATCH", "HQ did not keep the party name as typed");
    }
}

