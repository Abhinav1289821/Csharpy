using System.Reflection;
using Microsoft.Data.SqlClient;
using MK.Hq.Domain;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Tests.WritePath;

/// <summary>
/// HqSession's argument conversion (what reaches HQ's procedure parameters) and its SQL error translation (which errors
/// are retried, which are the operator's, which are uncertain). Database-free: SqlExceptions are built by reflection.
/// </summary>
public sealed class WritePathSessionTests
{
    private static readonly MethodInfo ConvertMethod = typeof(HqSession).GetMethod("Convert", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object Convert(string type, object? value, int maxLength = 0, byte precision = 0, byte scale = 0)
    {
        try { return ConvertMethod.Invoke(null, ["Proc", new ProcedureParameter("@p", type, maxLength, precision, scale, false), value])!; }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    // ---------------- Convert ----------------

    [Theory]
    [InlineData("smallint", 40000)]
    [InlineData("smallint", -40000)]
    [InlineData("tinyint", -1)]
    [InlineData("tinyint", 256)]
    [InlineData("int", 3_000_000_000L)]
    public void Whole_numbers_outside_the_parameter_type_are_refused_by_name(string type, long value)
    {
        // Old code: (short)40000m threw a bare OverflowException that no handler understood (a 500 to the operator).
        var e = Assert.Throws<HqException>(() => Convert(type, value));
        Assert.Equal(("HQ_FIELD_OUT_OF_RANGE", FailureKind.Invalid), (e.Code, e.Kind));
    }

    [Fact]
    public void Whole_numbers_inside_the_range_bind_as_the_parameter_type()
    {
        Assert.Equal((short)-32768, Convert("smallint", -32768));
        Assert.Equal((byte)255, Convert("tinyint", 255m));
        Assert.Equal(2_147_483_647, Convert("int", "2147483647"));
        Assert.Equal(long.MaxValue, Convert("bigint", long.MaxValue));
        Assert.Equal(12, Convert("int", 12.000m));
        Assert.Equal(HqException.Internal("x").Kind, Assert.Throws<HqException>(() => Convert("int", 1.5m)).Kind);
    }

    [Fact]
    public void Amounts_with_more_whole_digits_than_the_numeric_holds_are_refused_by_name()
    {
        var e = Assert.Throws<HqException>(() => Convert("numeric", 10_000_000_000_000_000m, precision: 18, scale: 2));
        Assert.Equal("HQ_FIELD_OUT_OF_RANGE", e.Code);
        Assert.Throws<HqException>(() => Convert("decimal", -1000m, precision: 5, scale: 2));
        Assert.Equal(9_999_999_999_999_999.99m, Convert("numeric", 9_999_999_999_999_999.99m, precision: 18, scale: 2));
        Assert.Equal(999.999m, Convert("decimal", 999.999m, precision: 5, scale: 2)); // scale is SqlClient's business (HQ parity)
        Assert.Equal(1e20m, Convert("numeric", 1e20m, precision: 38, scale: 4));
        Assert.Equal(123.45m, Convert("money", "123.45"));
    }

    [Theory]
    [InlineData('\u0080')]
    [InlineData('\u0096')]
    [InlineData('\u009F')]
    [InlineData('’')] // ’ (stored by SQL Server as 0x92, but refused as before: see report)
    [InlineData('₹')] // ₹
    [InlineData('ā')] // ā: SQL Server's best fit would silently store 'a'
    public void Varchar_refuses_characters_HQ_would_not_store_as_typed(char c)
    {
        // U+0080..U+009F passed the old "> 0xFF" check and SQL Server stored them as '?'.
        var e = Assert.Throws<HqException>(() => Convert("varchar", "AB" + c, maxLength: 50));
        Assert.Equal("HQ_FIELD_ENCODING", e.Code);
    }

    [Theory]
    [InlineData("Plain ASCII ~!@#")]
    [InlineData("Café Ñ ½ ° ×")]
    [InlineData("\u0081\u008D\u008F\u0090\u009D")]
    public void Varchar_accepts_what_code_page_1252_stores_unchanged(string text) =>
        Assert.Equal(text, Convert("varchar", text, maxLength: 50));

    [Fact]
    public void Nvarchar_takes_any_character_and_counts_its_limit_in_characters()
    {
        Assert.Equal("₹ ā", Convert("nvarchar", "₹ ā", maxLength: 6));
        Assert.Equal("HQ_FIELD_TOO_LONG", Assert.Throws<HqException>(() => Convert("nvarchar", "1234", maxLength: 6)).Code);
        Assert.Equal("HQ_FIELD_TOO_LONG", Assert.Throws<HqException>(() => Convert("varchar", "12345", maxLength: 4)).Code);
        Assert.Equal(new string('x', 9000), Convert("varchar", new string('x', 9000), maxLength: -1)); // varchar(max)
    }

    [Fact]
    public void Text_parameters_format_numbers_and_dates_invariantly()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1234.5", Convert("varchar", 1234.5m, maxLength: 20));
            Assert.Equal("0.25", Convert("varchar", 0.25d, maxLength: 20));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Dates_bind_as_midnight_or_as_given_and_null_binds_DBNull()
    {
        Assert.Equal(new DateTime(2026, 4, 1), Convert("datetime", new DateOnly(2026, 4, 1)));
        Assert.Equal(new DateTime(2027, 3, 31, 23, 59, 0), Convert("smalldatetime", "2027-03-31T23:59:00"));
        Assert.Equal(DBNull.Value, Convert("datetime", null));
        Assert.Equal(DBNull.Value, Convert("int", DBNull.Value));
        Assert.Equal(true, Convert("bit", 1));
        Assert.Equal(false, Convert("bit", 0m));
    }

    // ---------------- Translate ----------------

    [Fact]
    public void Guard_errors_carry_only_their_own_message_even_with_batch_warnings()
    {
        // SqlException.Message joins every error and warning of the batch; the guard text must not show the warning.
        var e = HqSession.Translate(Sql((50000, 16, "MK:INTEGRITY_ORDER|Do this first: fix the ledger"),
            (8153, 10, "Warning: Null value is eliminated by an aggregate or other SET operation.")));
        Assert.Equal(("INTEGRITY_ORDER", "Do this first: fix the ledger", FailureKind.Invalid), (e.Code, e.Message, e.Kind));
    }

    [Fact]
    public void Guard_error_is_found_when_it_is_not_the_first_error()
    {
        var e = HqSession.Translate(Sql((3621, 16, "The statement has been terminated."), (50000, 16, "MK:HQ_NUMBER_TAKEN|The bill number was just used by another PC")));
        Assert.Equal(("HQ_NUMBER_TAKEN", FailureKind.Conflict), (e.Code, e.Kind));
    }

    [Theory]
    [InlineData("MK:HQ_NUMBER_TAKEN|x", "HQ_NUMBER_TAKEN", FailureKind.Conflict)]
    [InlineData("MK:REVIEW_CHANGED|x", "REVIEW_CHANGED", FailureKind.Conflict)]
    [InlineData("MK:VOUCHER_ALLOCATION_STALE|x", "VOUCHER_ALLOCATION_STALE", FailureKind.Conflict)]
    [InlineData("MK:HQ_SAVE_MISMATCH|x", "HQ_SAVE_MISMATCH", FailureKind.Invalid)]
    [InlineData("MK:BARE_CODE", "BARE_CODE", FailureKind.Invalid)]
    public void Guard_codes_map_to_conflict_or_invalid(string message, string code, FailureKind kind)
    {
        var e = HqSession.Translate(Sql((50000, 16, message)));
        Assert.Equal((code, kind), (e.Code, e.Kind));
    }

    [Theory]
    [InlineData(1205, "HQ_DEADLOCK", FailureKind.Conflict)]
    [InlineData(1222, "HQ_BUSY", FailureKind.Conflict)]
    [InlineData(-2, "HQ_TIMEOUT", FailureKind.Unavailable)]
    [InlineData(53, "HQ_UNAVAILABLE", FailureKind.Unavailable)]
    [InlineData(18456, "HQ_UNAVAILABLE", FailureKind.Unavailable)]
    [InlineData(266, "HQ_TRANSACTION_LOST", FailureKind.Unavailable)]
    [InlineData(3903, "HQ_TRANSACTION_LOST", FailureKind.Unavailable)]
    [InlineData(8152, "HQ_FIELD_TOO_LONG", FailureKind.Invalid)]
    [InlineData(2628, "HQ_FIELD_TOO_LONG", FailureKind.Invalid)]
    [InlineData(229, "HQ_PERMISSION_DENIED", FailureKind.Unsupported)]
    [InlineData(2812, "HQ_PROCEDURE_MISSING", FailureKind.Unsupported)]
    [InlineData(8144, "HQ_PROCEDURE_CHANGED", FailureKind.Internal)]
    [InlineData(547, "HQ_SQL_ERROR", FailureKind.Unavailable)]
    public void Sql_error_numbers_map_to_business_errors(int number, string code, FailureKind kind)
    {
        var e = HqSession.Translate(Sql((number, 16, "server text")), "IQDoc_Header_Insert");
        Assert.Equal((code, kind), (e.Code, e.Kind));
        Assert.IsType<SqlException>(e.InnerException);
    }

    [Fact]
    public void Hq_raiserror_is_shown_in_hqs_own_words()
    {
        var e = HqSession.Translate(Sql((50000, 16, "Item already exists")), "IQItems_Mast_insert");
        Assert.Equal("HQ_REFUSED", e.Code);
        Assert.Contains("Item already exists", e.Message);
        Assert.Contains("IQItems_Mast_insert", e.Message);
    }

    /// <summary>Builds a SqlException with the given (number, class, message) errors, in order.</summary>
    internal static SqlException Sql(params (int Number, byte Class, string Message)[] errors)
    {
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ctor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).First();
        foreach (var (number, cls, message) in errors)
        {
            // SqlError(int infoNumber, byte errorState, byte errorClass, string server, string errorMessage, string procedure, int lineNumber, ...)
            var ints = 0; var bytes = 0; var strings = 0;
            var args = ctor.GetParameters().Select(p => p.ParameterType switch
            {
                var t when t == typeof(int) => (object?)(ints++ == 0 ? number : 1),
                var t when t == typeof(byte) => bytes++ == 0 ? (byte)1 : cls,
                var t when t == typeof(string) => strings++ switch { 0 => "server", 1 => message, _ => "" },
                var t when t == typeof(uint) => 0u,
                var t when t.IsValueType => Activator.CreateInstance(t),
                _ => null,
            }).ToArray();
            add.Invoke(collection, [ctor.Invoke(args)]);
        }
        var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "CreateException")
            .OrderBy(m => m.GetParameters().Length)
            .First(m => m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));
        var createArgs = create.GetParameters().Select((p, i) => i == 0 ? collection : p.ParameterType == typeof(string) ? "16.0" : p.HasDefaultValue ? p.DefaultValue
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return (SqlException)create.Invoke(null, createArgs)!;
    }
}
