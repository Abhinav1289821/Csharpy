namespace MK.Hq.Engine.Configuration;

/// <summary>Installation settings (appsettings.json section "Hq"). Nothing here comes from a browser.</summary>
public sealed class HqOptions
{
    public string Server { get; set; } = @"localhost\SQLEXPRESS";

    /// <summary>Windows authentication when empty; otherwise a SQL login.</summary>
    public string? UserId { get; set; }
    public string? Password { get; set; }

    /// <summary>HQ's ServerPath (DataSource.xml). Its UserLocks folder holds HQ's per-document edit locks.</summary>
    public string? HqServerPath { get; set; }

    /// <summary>Refuse amend/delete when HQ's UserLocks folder is unavailable (recommended in production).</summary>
    public bool RequireHqDocumentLocks { get; set; } = true;

    /// <summary>HQ operator id written to Ent_Uid/Modify_By. The shop uses HQ user 1.</summary>
    public int HqUserId { get; set; } = 1;

    /// <summary>Written to IQDoc_Header.BillEnteredSystem. HQ writes the PC name; default: this PC's name.</summary>
    public string? MachineName { get; set; }

    /// <summary>Folder for the engine's own state: operation journal, drafts, leases. Never inside HQ's database.</summary>
    public string DataDirectory { get; set; } = "var";

    /// <summary>Seconds a write waits for a lock held by HQ or another terminal before giving up.</summary>
    public int LockTimeoutSeconds { get; set; } = 15;

    /// <summary>Payment modes (Payment_Modes positions) allowed for new bills. 3 ("Others") needs HQ's split dialog.</summary>
    public int[] SalePaymentModes { get; set; } = [0, 1, 2, 4, 5, 6, 7];
    public int[] PurchasePaymentModes { get; set; } = [0, 1];

    public bool AllowInterstate { get; set; } = true;

    public List<FirmOptions> Firms { get; set; } = [];

    /// <summary>HQ's company-master database (ISTMAIN: companies, users, licence), read by data-health checks only.</summary>
    public string? CompanyDatabase { get; set; }

    /// <summary>Previous capture app's ai-settings.json, imported once when this app has no AI settings yet.</summary>
    public string? ImportAiSettingsFrom { get; set; }

    /// <summary>Phone uploads: the LAN port the QR code points at (0 disables phone uploads).</summary>
    public int PhoneUploadPort { get; set; } = 47351;

    public string EffectiveMachineName => string.IsNullOrWhiteSpace(MachineName) ? Environment.MachineName : MachineName!;
}

public sealed class FirmOptions
{
    /// <summary>Short stable id used in URLs ("mbm").</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Database { get; set; } = "";
    /// <summary>Financial year; derived from the database name (…26 → 2026-27) when omitted.</summary>
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    /// <summary>Optional printed letterhead lines.</summary>
    public string[] Letterhead { get; set; } = [];
    public string? Gstin { get; set; }
}
