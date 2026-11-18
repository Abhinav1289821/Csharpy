using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using MK.Hq.Api;

namespace MK.Hq.Tests.Api;

/// <summary>
/// The whole app started in memory with a throw-away data folder and an HQ server that does not exist, so start-up's
/// contract check and recovery fail fast (they are wrapped) and nothing touches SQL Server.
/// </summary>
public sealed class ApiAppFactory : WebApplicationFactory<Program>
{
    public const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef";
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "mkhq-api-tests", Guid.NewGuid().ToString("N"));

    public ApiAppFactory()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, "api.token"), Token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Hq:DataDirectory", DataDirectory);
        builder.UseSetting("Hq:Server", "127.0.0.1,1"); // nothing listens on port 1: refused at once
        builder.UseSetting("Hq:CompanyDatabase", "MKHQ_API_TEST_NONE");
        builder.UseSetting("Hq:Firms:0:Database", "MKHQ_API_TEST_NONE");
        builder.UseSetting("Hq:Firms:1:Database", "MKHQ_API_TEST_NONE");
        builder.UseSetting("Hq:PhoneUploadPort", "0");
        builder.UseSetting("Hq:ImportAiSettingsFrom", "");
        builder.UseSetting("Agent:ReadPhotos", "false");
    }
}

public sealed class ApiAuthTests(ApiAppFactory app) : IClassFixture<ApiAppFactory>
{
    [Theory]
    [InlineData("/api/health", true)]
    [InlineData("/API/Health", true)]
    [InlineData("/api/health/", true)]
    [InlineData("/api/health/drafts", false)]
    [InlineData("/api/health/drafts/abc", false)]
    [InlineData("/api/healthy", false)]
    [InlineData("/api/mbm/health/checks", false)]
    [InlineData("/api/firms", false)]
    public void Only_the_health_probe_is_open(string path, bool open) => Assert.Equal(open, ApiAuth.IsOpen(new PathString(path)));

    [Fact]
    public void Token_must_match_exactly()
    {
        const string t = ApiAppFactory.Token;
        Assert.True(ApiAuth.Accepts("Bearer " + t, null, t));
        Assert.True(ApiAuth.Accepts(null, t, t));
        Assert.True(ApiAuth.Accepts("Basic abc", t, t)); // not a bearer header: the ?token= (EventSource) still counts
        Assert.False(ApiAuth.Accepts("Bearer " + t[..^1], null, t));
        Assert.False(ApiAuth.Accepts("Bearer " + t + " ", null, t));
        Assert.False(ApiAuth.Accepts("bearer " + t, null, t));
        Assert.False(ApiAuth.Accepts("Bearer ", t, t)); // an empty bearer is a wrong key, not "fall back to the query"
        Assert.False(ApiAuth.Accepts(null, null, t));
        Assert.False(ApiAuth.Accepts(null, "", ""));    // a missing key file never means "open"
    }

    [Fact]
    public void Id_lists_parse_or_fail_as_bad_requests()
    {
        Assert.Equal([506, 507], ApiAuth.Ids<int>("506, 507,", "tt"));
        Assert.Equal(MK.Hq.Domain.FailureKind.Invalid, Assert.Throws<MK.Hq.Domain.HqException>(() => ApiAuth.Ids<int>("506,abc", "tt")).Kind);
        Assert.Equal(MK.Hq.Domain.FailureKind.Invalid, Assert.Throws<MK.Hq.Domain.HqException>(() => ApiAuth.Ids<long>("99999999999999999999", "ids")).Kind);
    }

    private HttpClient Client(string? token = null)
    {
        var c = app.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Health_is_open_and_the_app_starts_without_SQL_Server()
    {
        var r = await Client().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(File.Exists(Path.Combine(app.DataDirectory, "captures.db")), "the test data folder is the one in use");
    }

    [Theory]
    [InlineData("GET", "/api/firms")]
    [InlineData("GET", "/api/mbm/drafts")]
    [InlineData("GET", "/api/operations")]
    [InlineData("GET", "/api/settings/ai")]
    [InlineData("GET", "/api/agent")]
    [InlineData("POST", "/api/agent/chats")]
    [InlineData("GET", "/api/mbm/captures")]
    // routes under /api/{firm} with firm "health": these were open to anyone before.
    [InlineData("GET", "/api/health/drafts")]
    [InlineData("PUT", "/api/health/drafts/abc")]
    [InlineData("DELETE", "/api/health/drafts/abc")]
    [InlineData("GET", "/api/health/cleanup")]
    [InlineData("POST", "/api/health/cleanup/decide")]
    [InlineData("GET", "/api/HEALTH/drafts")]
    public async Task Every_api_route_needs_the_key(string method, string path)
    {
        foreach (var token in new string?[] { null, "wrong", ApiAppFactory.Token[..^1] + "X" })
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "DELETE" ? null : new StringContent("{}", Encoding.UTF8, "application/json") };
            var r = await Client(token).SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
    }

    [Fact]
    public async Task The_right_key_opens_a_route_by_header_or_query()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client(ApiAppFactory.Token).GetAsync("/api/mbm/drafts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/api/mbm/drafts?token=" + ApiAppFactory.Token)).StatusCode);
    }

    [Fact]
    public async Task A_bad_id_list_is_a_400_not_a_500()
    {
        var r = await Client(ApiAppFactory.Token).GetAsync("/api/mbm/cleanup/items?stage=all&ids=1,99999999999999999999");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer ")]
    [InlineData("Bearer 0123456789abcdef0123456789abcdef0123456789abcdef")] // the API key is not a chat key
    [InlineData("0123456789abcdef")]
    public async Task Mcp_needs_a_live_chat_key(string? authorization)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json") };
        if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        var r = await Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.DoesNotContain("\"tools\"", await r.Content.ReadAsStringAsync());
    }
}
