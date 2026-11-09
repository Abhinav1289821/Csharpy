using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using MK.Hq.Domain;

namespace MK.Hq.Engine.Sql;

public sealed record ProcedureParameter(string Name, string TypeName, int MaxLength, byte Precision, byte Scale, bool IsOutput)
{
    public SqlDbType DbType => TypeName.ToLowerInvariant() switch
    {
        "int" => SqlDbType.Int,
        "bigint" => SqlDbType.BigInt,
        "smallint" => SqlDbType.SmallInt,
        "tinyint" => SqlDbType.TinyInt,
        "bit" => SqlDbType.Bit,
        "float" => SqlDbType.Float,
        "real" => SqlDbType.Real,
        "money" => SqlDbType.Money,
        "smallmoney" => SqlDbType.SmallMoney,
        "decimal" or "numeric" => SqlDbType.Decimal,
        "datetime" => SqlDbType.DateTime,
        "smalldatetime" => SqlDbType.SmallDateTime,
        "date" => SqlDbType.Date,
        "varchar" => SqlDbType.VarChar,
        "char" => SqlDbType.Char,
        "nvarchar" => SqlDbType.NVarChar,
        "nchar" => SqlDbType.NChar,
        "text" => SqlDbType.Text,
        "ntext" => SqlDbType.NText,
        "image" => SqlDbType.Image,
        "varbinary" => SqlDbType.VarBinary,
        _ => throw HqException.Unsupported("HQ_PROCEDURE_CHANGED", $"Unsupported parameter type {TypeName}"),
    };

    /// <summary>Characters the parameter can carry without SQL Server silently truncating.</summary>
    public int? CharacterLimit => TypeName.ToLowerInvariant() switch
    {
        "varchar" or "char" => MaxLength,
        "nvarchar" or "nchar" => MaxLength < 0 ? -1 : MaxLength / 2,
        _ => null,
    };
}

public sealed record ProcedureSignature(string Name, IReadOnlyList<ProcedureParameter> Parameters, string DefinitionHash)
{
    private readonly Dictionary<string, ProcedureParameter> _byName =
        Parameters.ToDictionary(p => p.Name.TrimStart('@'), StringComparer.OrdinalIgnoreCase);

    public ProcedureParameter? Find(string name) => _byName.GetValueOrDefault(name.TrimStart('@'));
}

/// <summary>
/// Reads HQ's procedure signatures from sys.parameters once per database, and fingerprints each
/// definition. Every write goes through a signature, so a misspelt parameter, a text value that SQL
/// Server would truncate, or a vendor update that changed a procedure is caught before anything runs.
/// </summary>
public sealed class ProcedureCatalog
{
    /// <summary>The only HQ procedures this engine is allowed to execute.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "IQDoc_Header_Insert", "IQDoc_Delv_insert", "IQDoc_Recd_insert", "IQOrd_Detail_insert", "IQCash_TRAN_insert",
        "IQDOC_VAT_Insert", "IQDoc_Chrgs_Insert", "IQdaybook_insert", "UpdateClosing_Stock_B+NG",
        "User_IQDoc_Header_Insert", "User_IQDoc_Delv_insert", "User_IQDoc_Recd_insert", "User_IQDOC_VAT_Insert",
        "User_IQDoc_Chrgs_Insert", "User_IQCash_TRAN_insert", "User_IQdaybook_insert", "User_IQbnktran_insert",
        "Insert_Voudetails", "Update_Voudetails", "User_Insert_Voudetails", "IQbnktran_insert",
        "Outward_Delete", "Inward_Delete", "Orders_Delete",
        "IQaccmst_insert", "IQaccmst_update", "IQAccAdr_insert", "IQAccOthers_insert", "IQaccbdg_insert", "ttype_acnt_insert",
        "IQItems_Mast_insert", "IQItems_Mast_Update_ItemImages", "IQItems_Units_insert",
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, ProcedureSignature>>>> _byDatabase = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProcedureSignature> GetAsync(SqlConnection connection, SqlTransaction? transaction, string procedure)
    {
        Guard.Require(Allowed.Contains(procedure), "HQ_PROCEDURE_NOT_ALLOWED", $"{procedure} is not on the engine's allow-list", FailureKind.Internal);
        var all = await _byDatabase.GetOrAdd(connection.Database, _ => new(() => LoadAsync(connection, transaction))).Value;
        return all.TryGetValue(procedure, out var sig)
            ? sig
            : throw HqException.Unsupported("HQ_PROCEDURE_MISSING", $"HQ procedure {procedure} is missing from {connection.Database}");
    }

    public async Task<IReadOnlyDictionary<string, ProcedureSignature>> AllAsync(SqlConnection connection) =>
        await _byDatabase.GetOrAdd(connection.Database, _ => new(() => LoadAsync(connection, null))).Value;

    public void Invalidate(string database) => _byDatabase.TryRemove(database, out _);

    private static async Task<IReadOnlyDictionary<string, ProcedureSignature>> LoadAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        var rows = await connection.QueryAsync<(string Proc, string Definition, string? Param, string? TypeName, short MaxLength, byte Precision, byte Scale, bool IsOutput)>(
            """
            SELECT p.name, ISNULL(OBJECT_DEFINITION(p.object_id),''), pa.name, TYPE_NAME(pa.user_type_id), ISNULL(pa.max_length,0), ISNULL(pa.precision,0), ISNULL(pa.scale,0), ISNULL(pa.is_output,0)
            FROM sys.procedures p LEFT JOIN sys.parameters pa ON pa.object_id = p.object_id
            WHERE p.name IN @names ORDER BY p.name, pa.parameter_id
            """, new { names = Allowed.ToArray() }, transaction);
        return rows.GroupBy(r => r.Proc, StringComparer.OrdinalIgnoreCase).ToDictionary(
            g => g.Key,
            g =>
            {
                var parameters = g.Where(r => r.Param is not null)
                    .Select(r => new ProcedureParameter(r.Param!, r.TypeName!, r.MaxLength, r.Precision, r.Scale, r.IsOutput)).ToArray();
                var shape = string.Join("|", parameters.Select(p => $"{p.Name}:{p.TypeName}:{p.MaxLength}:{p.Precision}:{p.Scale}:{p.IsOutput}"));
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(g.First().Definition) + "\n" + shape)));
                return new ProcedureSignature(g.Key, parameters, hash);
            },
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whitespace and case do not change behaviour; ignore them in fingerprints.</summary>
    private static string Normalise(string definition) =>
        string.Join(' ', definition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
