using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using MK.Hq.Domain;

namespace MK.Hq.Engine.Sql;

/// <summary>
/// One SQL Server connection to an HQ year database, optionally inside one transaction.
/// Session settings make the engine a polite co-tenant of HQ:
/// <list type="bullet">
/// <item><c>XACT_ABORT ON</c>: any error dooms the whole transaction; nothing half-written can commit.</item>
/// <item><c>DEADLOCK_PRIORITY LOW</c>: if SQL Server must pick a victim between HQ and us, it picks us.
/// HQ's own saves are not transactional, so losing them would leave HQ half-saved; we just retry.</item>
/// <item><c>LOCK_TIMEOUT</c>: we never hang behind an HQ screen indefinitely.</item>
/// </list>
/// </summary>
public sealed class HqSession : IAsyncDisposable
{
    private readonly ProcedureCatalog _catalog;

    private HqSession(SqlConnection connection, ProcedureCatalog catalog)
    {
        Connection = connection;
        _catalog = catalog;
    }

    public SqlConnection Connection { get; }
    public SqlTransaction? Transaction { get; private set; }
    public string Database => Connection.Database;

    public static async Task<HqSession> OpenAsync(string connectionString, ProcedureCatalog catalog, int lockTimeoutSeconds, CancellationToken ct = default)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET XACT_ABORT ON; SET DEADLOCK_PRIORITY LOW; SET LOCK_TIMEOUT {lockTimeoutSeconds * 1000}; SET ARITHABORT ON; SET NOCOUNT ON;";
            await cmd.ExecuteNonQueryAsync(ct);
            return new HqSession(connection, catalog);
        }
        catch (SqlException e)
        {
            await connection.DisposeAsync();
            throw Translate(e);
        }
    }

    public async Task BeginAsync(IsolationLevel isolation = IsolationLevel.Serializable)
    {
        Guard.Invariant(Transaction is null, "A transaction is already open");
        Transaction = (SqlTransaction)await Connection.BeginTransactionAsync(isolation);
    }

    static HqSession() => DapperSetup.Ensure();

    public async Task CommitAsync()
    {
        Guard.Invariant(Transaction is not null, "No transaction to commit");
        await Transaction!.CommitAsync();
        await Transaction.DisposeAsync();
        Transaction = null;
    }

    /// <summary>
    /// Re-checks HQ's duplicate-number rule and commits in ONE server-side batch. Nothing — no client round trip, no
    /// journal write — sits between "this number is still free" and COMMIT, which shrinks the race with an HQ PC that
    /// read MAX(TNo) before our insert to the server's own execution time. If the number was taken, nothing commits.
    /// </summary>
    public async Task CommitAsync(IReadOnlyList<Plans.NumberCheck> finalChecks)
    {
        if (finalChecks.Count == 0) { await CommitAsync(); return; }
        Guard.Invariant(Transaction is not null, "No transaction to commit");
        var sql = new System.Text.StringBuilder();
        var p = new DynamicParameters();
        for (var i = 0; i < finalChecks.Count; i++)
        {
            var c = finalChecks[i];
            sql.Append($"IF EXISTS (SELECT 1 FROM dbo.IQDoc_Header WITH (READCOMMITTEDLOCK) WHERE TSeries=@s{i} AND TNo=@n{i} AND ISNULL(TExtn,'')=@e{i} AND Doc_Id<>@d{i} AND (@w{i}=1 OR Tt_Id=@t{i})) ")
               .Append("BEGIN RAISERROR('MK:HQ_NUMBER_TAKEN|The bill number was just used by another PC',16,1); RETURN; END; ");
            p.Add($"s{i}", c.Series); p.Add($"n{i}", c.Number); p.Add($"e{i}", c.Extension); p.Add($"d{i}", c.DocumentId); p.Add($"w{i}", c.SeriesWide ? 1 : 0); p.Add($"t{i}", c.TtId);
        }
        sql.Append("COMMIT TRANSACTION;");
        try
        {
            await Connection.ExecuteAsync(new CommandDefinition(sql.ToString(), p, Transaction, commandTimeout: 60));
        }
        catch (SqlException e)
        {
            throw Translate(e);
        }
        // The server committed; the client-side transaction object is complete and must not be used again.
        try { await Transaction!.DisposeAsync(); } catch (InvalidOperationException) { }
        Transaction = null;
    }

    public async Task RollbackAsync()
    {
        if (Transaction is null) return;
        try { await Transaction.RollbackAsync(); }
        catch (InvalidOperationException) { /* already rolled back by SQL Server (XACT_ABORT) */ }
        catch (SqlException) { /* connection broken: SQL Server rolls back on its own */ }
        await Transaction.DisposeAsync();
        Transaction = null;
    }

    /// <summary>HQ's procedures wrap their bodies in BEGIN TRAN/ROLLBACK; a ROLLBACK inside one of them would
    /// silently end our outer transaction. Check after every call that ours is still alive.</summary>
    public async Task AssertIntactAsync()
    {
        if (Transaction is null) return;
        var (count, state) = await Connection.QuerySingleAsync<(int, int)>("SELECT @@TRANCOUNT, XACT_STATE()", transaction: Transaction);
        Guard.Require(count == 1 && state == 1, "HQ_TRANSACTION_LOST", "An HQ procedure ended the transaction; nothing was saved", FailureKind.Unavailable);
    }

    /// <summary>A transaction-scoped SQL Server application lock: coordinates every MK terminal on the LAN
    /// without adding any object to HQ's database (locks live only in SQL Server's lock manager).</summary>
    public async Task AppLockAsync(string resource, int timeoutMs)
    {
        Guard.Invariant(Transaction is not null, "Application locks need a transaction");
        var p = new DynamicParameters();
        p.Add("@Resource", resource.Length > 255 ? resource[..255] : resource);
        p.Add("@LockMode", "Exclusive");
        p.Add("@LockOwner", "Transaction");
        p.Add("@LockTimeout", timeoutMs);
        p.Add("@result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
        await Connection.ExecuteAsync("sp_getapplock", p, Transaction, commandType: CommandType.StoredProcedure);
        var result = p.Get<int>("@result");
        Guard.Require(result >= 0, "OPERATION_BUSY", "Another MK terminal is saving the same thing; try again in a moment", FailureKind.Conflict);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? args = null)
    {
        try { return (await Connection.QueryAsync<T>(new CommandDefinition(sql, args, Transaction, commandTimeout: 60))).AsList(); }
        catch (SqlException e) { throw Translate(e); }
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? args = null)
    {
        try { return await Connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql, args, Transaction, commandTimeout: 60)); }
        catch (SqlException e) { throw Translate(e); }
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        try { return (await Connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, args, Transaction, commandTimeout: 60)))!; }
        catch (SqlException e) { throw Translate(e); }
    }

    public async Task<int> ExecuteAsync(string sql, object? args = null)
    {
        try { return await Connection.ExecuteAsync(new CommandDefinition(sql, args, Transaction, commandTimeout: 60)); }
        catch (SqlException e) { throw Translate(e); }
    }

    /// <summary>Rows as case-insensitive dictionaries (for audit projection and fingerprints).</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(string sql, object? args = null)
    {
        try
        {
            var rows = await Connection.QueryAsync(new CommandDefinition(sql, args, Transaction, commandTimeout: 60));
            return rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(
                ((IDictionary<string, object?>)r).Select(kv => KeyValuePair.Create(kv.Key, kv.Value is DBNull ? null : kv.Value)),
                StringComparer.OrdinalIgnoreCase)).ToList();
        }
        catch (SqlException e) { throw Translate(e); }
    }

    /// <summary>Calls an allow-listed HQ procedure with named arguments; returns its output parameters.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> CallAsync(string procedure, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var signature = await _catalog.GetAsync(Connection, Transaction, procedure);
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.[" + signature.Name + "]";
        cmd.CommandTimeout = 120;

        foreach (var (name, value) in args)
        {
            var p = signature.Find(name) ?? throw HqException.Internal($"{procedure} has no parameter @{name.TrimStart('@')}");
            cmd.Parameters.Add(Bind(procedure, p, value));
        }
        // Output parameters must be present even when the caller does not set them.
        foreach (var p in signature.Parameters.Where(p => p.IsOutput && !cmd.Parameters.Contains(p.Name)))
            cmd.Parameters.Add(Bind(procedure, p, null));

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException e)
        {
            throw Translate(e, procedure);
        }
        await AssertIntactAsync();
        return cmd.Parameters.Cast<SqlParameter>()
            .Where(p => p.Direction != ParameterDirection.Input)
            .ToDictionary(p => p.ParameterName.TrimStart('@'), p => p.Value is DBNull ? null : p.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static SqlParameter Bind(string procedure, ProcedureParameter p, object? value)
    {
        var parameter = new SqlParameter(p.Name, p.DbType)
        {
            Direction = p.IsOutput ? ParameterDirection.InputOutput : ParameterDirection.Input,
        };
        if (p.DbType == SqlDbType.Decimal) { parameter.Precision = p.Precision; parameter.Scale = p.Scale; }
        if (p.CharacterLimit is { } limit) parameter.Size = limit;
        parameter.Value = Convert(procedure, p, value);
        return parameter;
    }

    private static object Convert(string procedure, ProcedureParameter p, object? value)
    {
        if (value is null or DBNull) return DBNull.Value;
        switch (p.DbType)
        {
            case SqlDbType.VarChar or SqlDbType.Char or SqlDbType.NVarChar or SqlDbType.NChar or SqlDbType.Text or SqlDbType.NText:
                var text = value switch
                {
                    string s => s,
                    decimal d => d.ToString(CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? "",
                };
                if (p.CharacterLimit is > 0 and var limit && text.Length > limit)
                    throw HqException.Invalid("HQ_FIELD_TOO_LONG", $"A value for {p.Name.TrimStart('@')} is longer than HQ can store ({limit} characters)", new { procedure, parameter = p.Name });
                if (p.DbType is SqlDbType.VarChar or SqlDbType.Char && text.Any(c => c > 0xFF))
                    throw HqException.Invalid("HQ_FIELD_ENCODING", $"{p.Name.TrimStart('@')} contains characters HQ cannot store (use English letters)");
                return text;
            case SqlDbType.DateTime or SqlDbType.SmallDateTime or SqlDbType.Date:
                return value switch
                {
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    DateTime dt => dt,
                    string s => DateTime.Parse(s, CultureInfo.InvariantCulture),
                    _ => throw HqException.Internal($"{p.Name} expects a date"),
                };
            case SqlDbType.Bit:
                return value switch { bool b => b, _ => System.Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0 };
            case SqlDbType.Float or SqlDbType.Real:
                return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            case SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney:
                return value is string ds ? decimal.Parse(ds, CultureInfo.InvariantCulture) : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            case SqlDbType.Int or SqlDbType.BigInt or SqlDbType.SmallInt or SqlDbType.TinyInt:
                var number = value is string ns ? decimal.Parse(ns, CultureInfo.InvariantCulture) : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                Guard.Invariant(number == decimal.Truncate(number), $"{p.Name} expects a whole number");
                return p.DbType switch
                {
                    SqlDbType.BigInt => (object)(long)number,
                    SqlDbType.SmallInt => (short)number,
                    SqlDbType.TinyInt => (byte)number,
                    _ => (int)number,
                };
            default:
                return value;
        }
    }

    /// <summary>Turns SQL Server errors into business errors. Guard statements raise "MK:CODE|message".</summary>
    public static HqException Translate(SqlException e, string? procedure = null)
    {
        var where = procedure is null ? "" : $" ({procedure})";
        if (e.Message.StartsWith("MK:", StringComparison.Ordinal))
        {
            var body = e.Message[3..];
            var bar = body.IndexOf('|');
            var code = bar < 0 ? body : body[..bar];
            var message = bar < 0 ? body : body[(bar + 1)..];
            return new HqException(code, message, code.EndsWith("CHANGED") || code.EndsWith("TAKEN") || code.Contains("ALLOCATION") ? FailureKind.Conflict : FailureKind.Invalid, inner: e);
        }
        return e.Number switch
        {
            1222 => new HqException("HQ_BUSY", "HQ is busy with the same records (someone is saving in HQ). Try again in a moment." + where, FailureKind.Conflict, inner: e),
            1205 => new HqException("HQ_DEADLOCK", "SQL Server chose this save as a deadlock victim; nothing was written. Try again." + where, FailureKind.Conflict, inner: e),
            229 or 230 => new HqException("HQ_PERMISSION_DENIED", "The SQL login is not allowed to run an HQ procedure" + where, FailureKind.Unsupported, inner: e),
            2812 => new HqException("HQ_PROCEDURE_MISSING", "An HQ procedure is missing" + where, FailureKind.Unsupported, inner: e),
            266 or 3902 or 3903 => new HqException("HQ_TRANSACTION_LOST", "An HQ procedure rolled back; nothing was saved" + where, FailureKind.Unavailable, inner: e),
            -2 => new HqException("HQ_TIMEOUT", "SQL Server did not answer in time" + where, FailureKind.Unavailable, inner: e),
            53 or 2 or 4060 or 18456 or 40 or 10054 or 233 => new HqException("HQ_UNAVAILABLE", "Cannot reach the HQ database. Is SQL Server running?", FailureKind.Unavailable, inner: e),
            8152 or 2628 => new HqException("HQ_FIELD_TOO_LONG", "A value is longer than HQ can store" + where, FailureKind.Invalid, inner: e),
            // 201/8144: wrong arguments for an HQ procedure — an engine bug or a changed HQ build. Keep SQL's own text.
            201 or 8144 or 8145 => new HqException("HQ_PROCEDURE_CHANGED", $"HQ procedure call failed{where}: {e.Message}", FailureKind.Internal, inner: e),
            // RAISERROR text (HQ's own procedures, and the HQ form checks MK's repairs reproduce): say it in HQ's words.
            50000 => new HqException("HQ_REFUSED", $"HQ refuses this{where}: {e.Errors[0].Message}", FailureKind.Invalid, inner: e),
            _ => new HqException("HQ_SQL_ERROR", $"HQ rejected the operation{where}: SQL error {e.Number}", FailureKind.Unavailable, new { sql = e.Message }, e),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await RollbackAsync();
        await Connection.DisposeAsync();
    }
}
