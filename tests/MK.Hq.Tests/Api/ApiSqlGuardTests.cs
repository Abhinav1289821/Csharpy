using MK.Hq.Api.Agent;

namespace MK.Hq.Tests.Api;

/// <summary>
/// The assistant's db_query guard (AgentTools.RejectReason). Every dangerous statement must be refused; ordinary analytic
/// SELECTs must pass. The CR and nested-comment cases were checked against this PC's SQL Server: both ran hidden code
/// through the earlier regex guard.
/// </summary>
public sealed class ApiSqlGuardTests
{
    [Theory]
    [InlineData("SELECT TOP 10 Doc_Id, Bill_Amt FROM dbo.IQDoc_Header ORDER BY Doc_Id DESC")]
    [InlineData("select Tt_Id, COUNT(*) AS cnt, SUM(Bill_Amt) AS total FROM IQDoc_Header WHERE Doc_Date >= '2026-04-01' GROUP BY Tt_Id")]
    [InlineData("WITH s AS (SELECT Act_ID, SUM(AMOUNT) a FROM IQDayBook GROUP BY Act_ID) SELECT TOP 5 * FROM s ORDER BY a DESC")]
    [InlineData("SELECT Item_Name FROM IQItems_Mast WHERE Item_Name LIKE '%UPDATE%' OR Item_Name LIKE N'%drop%'")]
    [InlineData("SELECT [update], [delete] FROM (SELECT 1 [update], 2 [delete]) x")]
    [InlineData("SELECT \"set\" FROM (SELECT 1 \"set\") x")]
    [InlineData("SELECT 'it''s; -- not a comment /*' AS t")]
    [InlineData("SELECT 1 AS a -- a remark\n, 2 AS b")]
    [InlineData("SELECT 1 /* a remark */ AS a")]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT 1 ;  \r\n")]
    [InlineData("SELECT Doc_Id FROM IQDoc_Header ORDER BY Doc_Id OFFSET 10 ROWS FETCH NEXT 10 ROWS ONLY")]
    [InlineData("SELECT Update_Date, Set_No, Created_By FROM t")] // keywords inside longer names are fine
    [InlineData("SELECT 1 AS a\r\nUNION ALL\tSELECT 2")]
    [InlineData("SELECT CASE WHEN 1=1 THEN 'x' ELSE 'y' END")]
    [InlineData("SELECT * FROM OPENJSON(N'[1,2]')")]
    [InlineData("SELECT 1 --x\r' COMMIT DELETE FROM IQDayBook --'")] // CR ends the comment for both: the rest is a string alias
    [InlineData("SELECT 1; \t")]
    public void Ordinary_selects_pass(string sql) => Assert.Null(AgentTools.RejectReason(sql));

    [Theory]
    // plain writes and non-SELECT starts
    [InlineData("UPDATE IQAccmst SET Act_Name='x'")]
    [InlineData("DELETE FROM IQDayBook")]
    [InlineData("  exec sp_who")]
    [InlineData("(SELECT 1)")]
    [InlineData("GO\nSELECT 1")]
    // a second statement, with or without a semicolon
    [InlineData("SELECT 1; DELETE FROM IQDayBook")]
    [InlineData("SELECT 1 DELETE FROM IQDayBook")]
    [InlineData("SELECT 1\nGO\nDROP TABLE IQDayBook")]
    [InlineData("SELECT 1 COMMIT")]
    [InlineData("SELECT 1 WAITFOR DELAY '00:01:00'")]
    [InlineData("SELECT 1 EXEC('DELETE FROM IQDayBook')")]
    [InlineData("SELECT 1 EXECUTE ('DEL' + 'ETE FROM IQDayBook')")]
    [InlineData("SELECT 1 xp_cmdshell 'dir'")]
    [InlineData("SELECT * INTO #copy FROM IQDayBook")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENQUERY(srv, 'SELECT 1')")]
    [InlineData("WITH x AS (SELECT 1 a) INSERT INTO t SELECT a FROM x")]
    [InlineData("WITH x AS (SELECT 1 a) DELETE FROM x")]
    [InlineData("SELECT 1 DECLARE @x int")]
    [InlineData("SELECT 1 SET IMPLICIT_TRANSACTIONS OFF")]
    [InlineData("SELECT 1 BEGIN TRAN")]
    [InlineData("SELECT 1 SAVE TRAN x")]
    [InlineData("SELECT 1 DISABLE TRIGGER ALL ON IQDoc_Header")]
    [InlineData("SELECT 1 WRITETEXT t.c @p 'x'")]
    [InlineData("SELECT 1 RECONFIGURE")]
    [InlineData("SELECT 1 KILL 55")]
    [InlineData("SELECT * FROM IQDoc_Header WITH (TABLOCKX, HOLDLOCK)")]
    [InlineData("SELECT * FROM IQDoc_Header WITH (UPDLOCK)")]
    // strings, identifiers and comments that try to hide a write
    [InlineData("SELECT '--'; COMMIT; UPDATE IQAccmst SET Act_Name='x'")]
    [InlineData("SELECT 1 -- '\nDELETE FROM IQDayBook --'")]
    [InlineData("SELECT [--]; DELETE FROM IQDayBook")]
    [InlineData("SELECT \"--\" DELETE FROM IQDayBook")]
    [InlineData("SELECT N'x'' -- ' DELETE FROM IQDayBook")]
    [InlineData("SELECT '/*' DELETE FROM IQDayBook --*/")]
    [InlineData("SELECT 1 /* x */ DELETE FROM IQDayBook")]
    // SQL Server ends a -- comment at a lone CR: the regex guard read everything after it as comment.
    [InlineData("SELECT 1 --x\rCOMMIT DELETE FROM IQDayBook")]
    // /* comments nest in SQL Server and a quote inside one is not a string: these hid COMMIT from the regex guard.
    [InlineData("SELECT 1 /* /* */ ' */ COMMIT DELETE FROM IQDayBook --'")]
    [InlineData("SELECT 1 /* a /*/ ' */ */ COMMIT DELETE FROM IQDayBook --'")]
    [InlineData("SELECT 1 /* /* */ */")]
    // unclosed or unreadable
    [InlineData("SELECT 'abc")]
    [InlineData("SELECT [abc")]
    [InlineData("SELECT \"abc")]
    [InlineData("SELECT 1 /* never closed")]
    [InlineData("SELECT 'a'';DELETE FROM t;--")]
    [InlineData("SELECT 1\0; DELETE FROM t")]
    [InlineData("SELECT 1\v\nDELETE FROM t")]
    [InlineData("select 1 dElEtE from t")]
    public void Dangerous_statements_are_refused(string sql) => Assert.NotNull(AgentTools.RejectReason(sql));

    /// <summary>Unicode spaces before keywords (SQL Server treats U+00A0 and U+2028 as spaces; checked). Written as
    /// {NBSP}/{LS} placeholders so the source file stays plain ASCII.</summary>
    [Theory]
    [InlineData("SELECT 1{NBSP}DELETE{NBSP}FROM IQDayBook")]
    [InlineData("SELECT 1{LS}COMMIT")]
    [InlineData("{NBSP}{LS}UPDATE t SET a=1")]
    public void Unicode_spaces_do_not_hide_keywords(string sql) =>
        Assert.NotNull(AgentTools.RejectReason(sql.Replace("{NBSP}", ((char)0xA0).ToString()).Replace("{LS}", ((char)0x2028).ToString())));

    [Fact]
    public void Unicode_line_separator_does_not_end_a_line_comment() =>
        // SQL Server keeps reading the comment past U+2028 (checked), so this is one harmless SELECT.
        Assert.Null(AgentTools.RejectReason("SELECT 1 --x" + (char)0x2028 + "DELETE FROM t"));

    [Fact]
    public void Semicolon_inside_a_string_is_not_a_second_statement() =>
        Assert.Null(AgentTools.RejectReason("SELECT Act_Name FROM IQAccmst WHERE Act_Name = 'A; B'"));

    [Fact]
    public void Keywords_inside_strings_and_identifiers_are_data() =>
        Assert.Null(AgentTools.RejectReason("SELECT 'DELETE FROM x; COMMIT' AS [drop table], N'exec' AS \"insert\""));
}
