using System.Data;
using Dapper;

namespace MK.Hq.Engine.Sql;

/// <summary>Teaches Dapper the engine's value types (SQL date ↔ DateOnly, one-character codes ↔ char).</summary>
public static class DapperSetup
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
        SqlMapper.AddTypeHandler(new CharHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => DateOnly.FromDateTime(Convert.ToDateTime(value));
        public override void SetValue(IDbDataParameter parameter, DateOnly value) { parameter.DbType = DbType.Date; parameter.Value = value.ToDateTime(TimeOnly.MinValue); }
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override DateOnly? Parse(object value) => value is null or DBNull ? null : DateOnly.FromDateTime(Convert.ToDateTime(value));
        public override void SetValue(IDbDataParameter parameter, DateOnly? value) { parameter.DbType = DbType.Date; parameter.Value = value?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value; }
    }

    private sealed class CharHandler : SqlMapper.TypeHandler<char>
    {
        public override char Parse(object value) => value is string s && s.Length > 0 ? s.TrimStart()[0] : Convert.ToChar(value);
        public override void SetValue(IDbDataParameter parameter, char value) { parameter.DbType = DbType.String; parameter.Value = value.ToString(); }
    }
}
