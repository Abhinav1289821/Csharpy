using System.Security.Cryptography;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Hq;

namespace MK.Hq.Api;

/// <summary>The local access key the launcher hands to the browser (in the URL fragment, never sent to a server log).</summary>
public static class ApiToken
{
    public static string LoadOrCreate(string dataDirectory, string fileName = "api.token")
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, fileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(path, token);
        return token;
    }
}

/// <summary>Which /api requests need the access key, and whether a request carries it.</summary>
public static class ApiAuth
{
    /// <summary>Only the health probe itself ("/api/health", any case, optional trailing slash) is open; nothing under it.</summary>
    public static bool IsOpen(PathString path) =>
        path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/health/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The key from "Authorization: Bearer …" (or ?token= for EventSource, which cannot send headers), compared
    /// in constant time.</summary>
    public static bool Accepts(string? authorization, string? queryToken, string token)
    {
        var header = authorization ?? "";
        var supplied = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : queryToken ?? "";
        return token.Length > 0 && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(token));
    }

    /// <summary>A comma-separated id list from a query string; a bad entry is the caller's mistake (400), not a crash.</summary>
    public static List<T> Ids<T>(string list, string name) where T : struct, IParsable<T> =>
        list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => T.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : throw HqException.Invalid("BAD_REQUEST", $"'{x}' in {name} is not a valid id"))
            .ToList();
}

public sealed record IntegrityFinding(string Severity, string Title, string Detail, int Count, IReadOnlyList<string> Examples);

/// <summary>
/// A read-only health check of the HQ database itself: problems HQ's non-transactional saves or configuration have
/// left behind. Nothing here is "fixed" automatically; each finding says what to correct in HQ.
/// </summary>
public static class Integrity
{
    public static async Task<object> CheckAsync(HqFirm firm, HqContract contract, CancellationToken ct)
    {
        var findings = new List<IntegrityFinding>();
        var status = await contract.CheckAsync(firm, ct: ct);
        findings.Add(new IntegrityFinding(status.WritesAllowed ? "ok" : "error", "HQ procedure contract", status.Summary,
            status.ChangedProcedures.Count + status.MissingProcedures.Count, status.ChangedProcedures.Concat(status.MissingProcedures).ToList()));

        await using var s = await firm.OpenAsync(ct);
        async Task Add(string severity, string title, string detail, string sql)
        {
            var rows = await s.QueryAsync<string>(sql);
            findings.Add(new IntegrityFinding(rows.Count == 0 ? "ok" : severity, title, detail, rows.Count, rows.Take(12).ToList()));
        }

        await Add("warning", "Repeated bill numbers", "Two documents share a series and number (happens when two HQ PCs save at the same moment).",
            "SELECT RTRIM(TSeries)+'/'+CAST(TNo AS varchar(12))+' ×'+CAST(COUNT(*) AS varchar(5)) FROM dbo.IQDoc_Header WHERE Tt_Id NOT IN (222) GROUP BY TSeries, TNo, ISNULL(TExtn,'') HAVING COUNT(*)>1");
        await Add("error", "Bills without a ledger", "A sale or purchase has no IQDayBook rows: HQ's save stopped half-way. Open and re-save the bill in HQ.",
            "SELECT RTRIM(h.TSeries)+'/'+CAST(h.TNo AS varchar(12))+' ('+CONVERT(varchar(10),h.Doc_Date,103)+')' FROM dbo.IQDoc_Header h WHERE h.Tt_Id IN (500,501,506,507) AND ISNULL(h.Bill_Amt,0)<>0 AND NOT EXISTS (SELECT 1 FROM dbo.IQDayBook d WHERE d.Doc_ID=h.Doc_Id)");
        await Add("warning", "Orders without HQ's tracking rows", "A sales/purchase order has no User_IQ* audit copy or VAT row: an interrupted HQ save.",
            "SELECT RTRIM(h.TSeries)+'/'+CAST(h.TNo AS varchar(12)) FROM dbo.IQDoc_Header h WHERE h.Tt_Id IN (248,249) AND NOT EXISTS (SELECT 1 FROM dbo.IQDOC_VAT v WHERE v.Doc_Id=h.Doc_Id)");
        await Add("warning", "Round-off charge configured as 'Less'",
            "HQ posts round-off with the charge type's sign. Configured as 'Less', every rounded bill's ledger is off by twice the round-off. Change the Round Off charge type to 'Add' in HQ's charge master to correct future bills.",
            "SELECT DISTINCT RTRIM(c.Chrg_Name)+' (charge '+CAST(c.Chrg_Id AS varchar(6))+', set '+CAST(cs.Set_No AS varchar(6))+')' FROM dbo.IQCHARGES c JOIN dbo.IQCHRGSETS cs ON cs.Chrg_Id=c.Chrg_Id JOIN dbo.IQCHRG_TYPES t ON t.ChTp_ID=c.Chrg_Type JOIN dbo.TTypes tt ON tt.vousetno=cs.Set_No AND tt.RoundedOffTo>0 WHERE ISNULL(t.ChTp_Op,0)=0 AND c.Chrg_Name LIKE '%round%'");
        await Add("warning", "Unbalanced bill ledgers", "Debits and credits of these bills differ (mostly the round-off effect above).",
            "SELECT RTRIM(h.TSeries)+'/'+CAST(h.TNo AS varchar(12))+' diff '+CAST(CAST(ABS(x.n) AS decimal(19,2)) AS varchar(20)) FROM dbo.IQDoc_Header h CROSS APPLY (SELECT SUM(CASE WHEN d.TYPE IN ('D','P') THEN d.AMOUNT ELSE -d.AMOUNT END) + CASE WHEN h.Mode=0 THEN (CASE WHEN h.Tt_Id IN (506,507) THEN h.Bill_Amt ELSE -h.Bill_Amt END) ELSE 0 END n FROM dbo.IQDayBook d WHERE d.Doc_ID=h.Doc_Id) x WHERE h.Tt_Id IN (500,501,506,507) AND ABS(x.n) > 0.005");
        await Add("info", "Active items without a sales ledger", "These items cannot be billed until a sales account is set for them in HQ (Ttype_Acnt).",
            "SELECT LTRIM(RTRIM(im.Item_Name)) FROM dbo.IQItems_Mast im WHERE ISNULL(im.Item_Stat,0)=0 AND NOT EXISTS (SELECT 1 FROM dbo.Ttype_Acnt t WHERE t.Ic_Id=im.Item_ID AND t.TT_ID=506)");
        var gstins = await s.QueryAsync<(string Name, string Gstin)>("SELECT LTRIM(RTRIM(a.Act_Name)), UPPER(LTRIM(RTRIM(ISNULL(ad.GSTIN,'')))) FROM dbo.IQAccmst a JOIN dbo.IQAccAdr ad ON ad.Act_Id=a.Act_Id WHERE ISNULL(ad.GSTTaxType,2) IN (0,1) AND ISNULL(a.Act_Usage,0)=0");
        var bad = gstins.Where(g => !Gstin.IsValid(g.Gstin)).Select(g => $"{g.Name}: '{g.Gstin}'").ToList();
        findings.Add(new IntegrityFinding(bad.Count == 0 ? "ok" : "warning", "Registered parties with an invalid GSTIN",
            "The GSTIN fails the official check digit; GST returns will reject it. Correct it in the account master.", bad.Count, bad.Take(12).ToList()));
        return new { firm = firm.Id, checkedAt = DateTime.UtcNow, findings };
    }
}
