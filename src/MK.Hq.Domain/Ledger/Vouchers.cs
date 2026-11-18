namespace MK.Hq.Domain.Ledger;

public enum VoucherKind { CashReceipt, BankReceipt, CashPayment, BankPayment, CreditNote, DebitNote }

/// <summary>HQ's TTYPES rows for each voucher kind (verified in both firms).</summary>
public sealed record VoucherSpec(VoucherKind Kind, int TtId, string VouchType, int Mode)
{
    public bool IsBank => Mode == 1;
    public bool IsReceipt => VouchType == "Receipts";
    public bool IsPayment => VouchType == "Payments";
    public bool IsNote => !IsReceipt && !IsPayment;

    public static readonly IReadOnlyDictionary<VoucherKind, VoucherSpec> All = new Dictionary<VoucherKind, VoucherSpec>
    {
        [VoucherKind.CashReceipt] = new(VoucherKind.CashReceipt, 204, "Receipts", 0),
        [VoucherKind.BankReceipt] = new(VoucherKind.BankReceipt, 205, "Receipts", 1),
        [VoucherKind.CashPayment] = new(VoucherKind.CashPayment, 206, "Payments", 0),
        [VoucherKind.BankPayment] = new(VoucherKind.BankPayment, 207, "Payments", 1),
        [VoucherKind.CreditNote] = new(VoucherKind.CreditNote, 208, "Credit Note", 0),
        [VoucherKind.DebitNote] = new(VoucherKind.DebitNote, 209, "Debit Note", 0),
    };

    public static VoucherSpec For(VoucherKind kind) => All[kind];
    public static VoucherSpec? ForTt(int ttId) => All.Values.FirstOrDefault(v => v.TtId == ttId);
}

public sealed record VoucherAccount(long Id, string Name, string Type, bool BillWise)
{
    public bool IsCash => Type == "Cash";
}

public sealed record VoucherLine(long AccountId, decimal Amount, string Notes);

/// <summary>A voucher ledger row; <see cref="Line"/> is the input line it belongs to, -1 for the header row.</summary>
public sealed record VoucherLedgerRow(long AccountId, char Type, long ContraId, decimal Amount, string Notes, int Line);

/// <summary>Mirrors FrmFin_Vouchers.SaveDaybookData, including HQ's cash-transfer contra rows.</summary>
public static class VoucherLedger
{
    public static IReadOnlyList<VoucherLedgerRow> Plan(VoucherSpec spec, VoucherAccount header, IReadOnlyList<VoucherAccount> accounts, IReadOnlyList<VoucherLine> lines)
    {
        Guard.Invariant(accounts.Count == lines.Count, "Voucher accounts and lines disagree");
        var cashHeader = header.IsCash;
        var lineType = spec.Kind switch
        {
            VoucherKind.CreditNote => 'D',
            VoucherKind.DebitNote => 'C',
            _ when cashHeader => spec.IsReceipt ? 'R' : 'P',
            _ => spec.IsReceipt ? 'C' : 'D',
        };

        var rows = new List<VoucherLedgerRow>();
        decimal headerTotal = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var account = accounts[i];
            Guard.Require(account.Id == line.AccountId && account.Id != header.Id, "HQ_DATA_INVALID", "A voucher line cannot use the header account");
            var cashTransfer = (spec.IsReceipt || spec.IsPayment) && account.IsCash;
            rows.Add(new VoucherLedgerRow(
                cashTransfer ? header.Id : line.AccountId,
                cashTransfer ? (spec.IsReceipt ? 'P' : 'R') : lineType,
                cashTransfer ? line.AccountId : header.Id,
                line.Amount,
                line.Notes,
                i));
            if (!cashTransfer) headerTotal += line.Amount;
        }

        if ((!cashHeader || spec.IsNote) && headerTotal > 0)
        {
            var type = spec.Kind switch
            {
                VoucherKind.CreditNote => 'C',
                VoucherKind.DebitNote => 'D',
                // HQ's aType2 depends on receipt/payment only, not on the mode: a receipt's header is always debited.
                _ => spec.IsReceipt ? 'D' : 'C',
            };
            rows.Add(new VoucherLedgerRow(header.Id, type, lines[0].AccountId, headerTotal, lines[0].Notes, -1));
        }
        return rows;
    }

    /// <summary>
    /// The User_IQDayBook audit rows HQ writes for a voucher: one per line, two for bank and note lines
    /// (the second carrying DiscRow 'D'). Reproduced from the prototype's verified mapping.
    /// </summary>
    public static IReadOnlyList<(VoucherLedgerRow Row, int Counter)> AuditRows(VoucherSpec spec, VoucherAccount header, IReadOnlyList<VoucherAccount> accounts, IReadOnlyList<VoucherLine> lines)
    {
        var result = new List<(VoucherLedgerRow, int)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var account = accounts[i];
            var line = lines[i];
            var bank = spec.IsBank;
            var cashLine = account.IsCash;
            var count = (bank && !cashLine) || spec.IsNote ? 2 : 1;
            for (var counter = 0; counter < count; counter++)
            {
                var bankCounter = bank && !cashLine && counter == 1;
                char type = spec.IsNote
                    ? (spec.Kind == VoucherKind.CreditNote ? (counter == 0 ? 'C' : 'D') : (counter == 0 ? 'D' : 'C'))
                    : bank
                        ? (cashLine ? (spec.IsReceipt ? 'P' : 'R') : (spec.IsReceipt ? (counter == 0 ? 'C' : 'D') : (counter == 0 ? 'D' : 'C')))
                        : (spec.IsReceipt ? 'R' : 'P');
                var swap = bankCounter || (bank && cashLine) || (spec.IsNote && counter == 0);
                result.Add((new VoucherLedgerRow(
                    swap ? header.Id : line.AccountId,
                    type,
                    swap ? line.AccountId : header.Id,
                    line.Amount,
                    string.IsNullOrEmpty(line.Notes) ? " " : line.Notes,
                    i), counter));
            }
        }
        return result;
    }
}
