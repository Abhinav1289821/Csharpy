using MK.Hq.Api.Agent;

namespace MK.Hq.Tests.Api;

/// <summary>AgentHub.DecidePermission: what the assistant may do on the shop PC without asking.</summary>
public sealed class ApiPermissionTests
{
    [Theory]
    // MK's own tools, as Antigravity titles them (MCP name "mcp_mk_<tool>"), run without asking.
    [InlineData("other", "Run mcp_mk_find_items")]
    [InlineData("other", "mcp_mk_db_query")]
    [InlineData(null, "mcp_mk_stock")]
    [InlineData("fetch", "Run mcp_mk_party_ledger")]
    [InlineData("other", "mcp_mk_open_screen: Open reports screen")]
    [InlineData("other", "stock")]
    [InlineData("think", "find_parties (Search customers)")]
    public void Mk_tools_are_allowed(string? kind, string title) => Assert.Equal(PermissionDecision.Allow, AgentHub.DecidePermission(kind, title));

    [Theory]
    // Commands and file changes are refused, whatever they mention, in any letter case.
    [InlineData("execute", "Run mcp_mk_stock")]
    [InlineData("execute", "echo stock && del C:\\x")]
    [InlineData("Execute", "dir")]
    [InlineData(" EDIT ", "stock")]
    [InlineData("delete", "Delete stock.txt")]
    [InlineData("move", "Move day_book.csv")]
    public void Commands_and_file_changes_are_refused(string kind, string title) => Assert.Equal(PermissionDecision.Refuse, AgentHub.DecidePermission(kind, title));

    [Theory]
    // A tool name somewhere in the title is not an MK tool call: these used to be waved through.
    [InlineData("fetch", "Fetch https://example.com/stock?data=all")]
    [InlineData("read", "Read C:\\Users\\shop\\stock.txt")]
    [InlineData("read", "stock")]                    // a file called "stock" is still a file read
    [InlineData("search", "db_query")]
    [InlineData("other", "read_url_content https://x/outstanding")]
    [InlineData("other", "Run mcp_mk_drop_everything")] // not one of MK's tools
    [InlineData("other", "mcp_other_stock")]
    [InlineData("other", "stock.txt")]
    [InlineData("other", "an action")]
    [InlineData("other", "")]
    [InlineData(null, null)]
    public void Everything_else_is_the_users_call(string? kind, string? title) => Assert.Equal(PermissionDecision.Ask, AgentHub.DecidePermission(kind, title));
}
