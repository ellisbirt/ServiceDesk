using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Helpdesk.Api.Tests;

public class StatusEndpointTests
{
    private static HttpRequestMessage Get(string? roles = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/status");
        req.Headers.Add("X-Dev-Tenant", "t1");
        req.Headers.Add("X-Dev-User", "u1");
        if (roles is not null) req.Headers.Add("X-Dev-Roles", roles);
        return req;
    }

    private static async Task<(HttpStatusCode Code, JsonElement Body, string Raw)> Read(HttpResponseMessage res)
    {
        var raw = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, JsonDocument.Parse(raw).RootElement.Clone(), raw);
    }

    [Fact]
    public async Task DevelopmentDefaults_AreReportedWithoutFailures()
    {
        using var f = new ApiFactory();
        var (code, body, _) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.NotEqual("invalid", body.GetProperty("status").GetString());
        var areas = body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("area").GetString()).Distinct().ToList();
        Assert.Equal(["Auth", "Anthropic", "Search", "Queue", "Dlp", "Chat", "Hub", "Conversation", "Web", "Http"], areas);
    }

    [Theory]
    [InlineData("Dlp:Destinations:Nowhere:Email", "Redact", "not a destination")]
    [InlineData("Dlp:Destinations:Model:Birthday", "Redact", "not a category")]
    [InlineData("Dlp:Destinations:Model:Email", "Maybe", "must be Redact or Allow")]
    [InlineData("Dlp:Destinations:Queue:Secret", "Allow", "cannot be Allow")]
    public async Task InvalidDlpPolicy_IsReported(string key, string value, string expected)
    {
        using var f = new ApiFactory().WithWebHostBuilder(b => b.UseSetting(key, value));
        var (code, body, _) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        Assert.Contains(body.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("area").GetString() == "Dlp"
                && c.GetProperty("status").GetString() == "fail"
                && c.GetProperty("message").GetString()!.Contains(expected));
    }

    [Theory]
    [InlineData("Hub:MaxTicketsPerUser", "1")]   // a connection needs two tickets
    [InlineData("Hub:TicketSeconds", "3600")]    // a logged URL would stay useful for an hour
    [InlineData("Hub:TicketSeconds", "1")]
    [InlineData("RateLimit:TicketPerMinute", "1")]
    [InlineData("Hub:MaxTickets", "1")]
    public async Task UnusableHubTicketSettings_AreReported(string key, string value)
    {
        using var f = new ApiFactory().WithWebHostBuilder(b => b.UseSetting(key, value));
        var (code, body, _) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        Assert.Contains(body.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("area").GetString() is "Hub" && c.GetProperty("status").GetString() == "fail");
    }

    [Theory]
    [InlineData("Chat:MaxJobsPerTenant", "0")]
    [InlineData("Chat:FinalEventTimeoutSeconds", "0")]
    public async Task UnusableJobLimits_AreReported(string key, string value)
    {
        using var f = new ApiFactory().WithWebHostBuilder(b => b.UseSetting(key, value));
        var (code, body, _) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        Assert.Contains(body.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "TenantLimit" && c.GetProperty("status").GetString() == "fail");
    }

    [Fact]
    public async Task ATenantLimitThatCannotProtectAnyone_IsAWarning_NotAFailure()
    {
        using var f = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Chat:MaxJobsPerTenant", "100000"));
        var (code, body, _) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Contains(body.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "TenantLimitProtects" && c.GetProperty("status").GetString() == "warn");
    }

    [Fact]
    public async Task TooSmallHubMessageLimit_IsReported()
    {
        using var f = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Chat:MaxReceiveMessageBytes", "16384"));
        var (code, body, _) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        Assert.Contains(body.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "ReceiveLimit" && c.GetProperty("status").GetString() == "fail");
    }

    [Fact]
    public async Task InvalidSetting_ReturnsFailedCheck_AndServiceUnavailable()
    {
        using var f = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Anthropic:RateLimitMaxRetries", "-1"));
        var (code, body, _) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        Assert.Equal("invalid", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "RateLimits" && c.GetProperty("status").GetString() == "fail");
    }

    [Fact]
    public async Task Report_NeverContainsSecretValues()
    {
        using var f = new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("Anthropic:ApiKey", "sk-ant-SECRETKEYVALUE12345");
            b.UseSetting("StorageQueue:ConnectionString",
                "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=U0VDUkVUU1RPUkFHRUtFWQ==;EndpointSuffix=core.windows.net");
            b.UseSetting("Search:Provider", "Tavily");
            b.UseSetting("Search:Providers:Tavily:ApiKey", "tvly-SECRETSEARCHKEY");
        });
        var (_, _, raw) = await Read(await f.CreateClient().SendAsync(Get()));

        Assert.DoesNotContain("SECRETKEYVALUE", raw);
        Assert.DoesNotContain("U0VDUkVUU1RPUkFHRUtFWQ", raw);
        Assert.DoesNotContain("SECRETSEARCHKEY", raw);
    }

    private static WebApplicationFactory<Program> Production(Action<IWebHostBuilder> configure, bool signingKey = true) =>
        new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:UseDevAuth", "false");
            b.UseSetting("Anthropic:ApiKey", "test-key");
            b.UseSetting("Queue:Provider", "ServiceBus");
            b.UseSetting("ServiceBus:FullyQualifiedNamespace", "example.servicebus.windows.net");
            b.UseSetting("Auth:Authority", "https://tenant.ciamlogin.com/id/v2.0");
            b.UseSetting("Auth:Audience", "api://helpdesk");
            if (signingKey)
            {
                b.UseSetting("Conversation:ActiveKeyId", "k1");
                b.UseSetting("Conversation:Keys:k1", Convert.ToBase64String(new byte[32]));
            }
            configure(b);
        });

    [Fact]
    public void Production_WithInvalidConfiguration_RefusesToStart()
    {
        using var f = Production(b => b.UseSetting("Auth:Authority", ""));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Auth/Authority", ex.ToString());
        Assert.DoesNotContain("test-key", ex.ToString());
    }

    [Fact]
    public void Production_WithoutASigningKey_RefusesToStart()
    {
        using var f = Production(_ => { }, signingKey: false);
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Conversation/Keys", ex.ToString());
    }

    [Fact]
    public void Production_WithValidConfiguration_Starts()
    {
        using var f = Production(_ => { });
        using var client = f.CreateClient();
    }

    [Fact]
    public async Task CallerWithoutTheOperatorRole_IsForbidden()
    {
        using var f = new ApiFactory();
        var res = await f.CreateClient().SendAsync(Get(roles: "Helpdesk.User"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        using var f = new ApiFactory(devAuth: false);
        var res = await f.CreateClient().GetAsync("/status");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
