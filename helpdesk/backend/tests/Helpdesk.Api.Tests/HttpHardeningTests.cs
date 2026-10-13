using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Tests;

/// <summary>Sets the connection's client address from a test header, standing in for the gateway in front of the app.</summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, nxt) =>
        {
            ctx.Connection.RemoteIpAddress = ctx.Request.Headers.TryGetValue(Header, out var ip)
                ? IPAddress.Parse(ip!)
                : IPAddress.Parse("203.0.113.50");
            return nxt(ctx);
        });
        next(app);
    };
}

public sealed class HttpHardeningTests : IDisposable
{
    private static readonly Uri HttpsBase = new("https://helpdesk.example.com");
    private static readonly Uri HttpBase = new("http://helpdesk.example.com");
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static WebApplicationFactory<Program> WithClientIp(WebApplicationFactory<Program> f) =>
        f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, RemoteIpStartupFilter>()));

    private static WebApplicationFactory<Program> Production(Action<IWebHostBuilder>? configure = null) =>
        WithClientIp(new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:UseDevAuth", "false");
            b.UseSetting("Anthropic:ApiKey", "test-key");
            b.UseSetting("Queue:Provider", "ServiceBus");
            b.UseSetting("ServiceBus:FullyQualifiedNamespace", "example.servicebus.windows.net");
            b.UseSetting("Auth:Authority", "https://tenant.ciamlogin.com/id/v2.0");
            b.UseSetting("Auth:Audience", "api://helpdesk");
            b.UseSetting("Conversation:ActiveKeyId", "k1");
            b.UseSetting("Conversation:Keys:k1", Convert.ToBase64String(new byte[32]));
            configure?.Invoke(b);
        }));

    private static HttpClient Client(WebApplicationFactory<Program> f, Uri? baseAddress = null) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = baseAddress ?? HttpsBase, AllowAutoRedirect = false });

    private static HttpRequestMessage Chat(string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/message")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Dev-Tenant", "tenant-a");
        req.Headers.Add("X-Dev-User", "user-1");
        return req;
    }

    private static string ChatJson(int padTo = 0)
    {
        var json = JsonSerializer.Serialize(new { messageText = "VPN is slow", conversationId = Guid.NewGuid() });
        return padTo > json.Length ? json[..^1] + new string(' ', padTo - json.Length) + "}" : json;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage res)
    {
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static void AssertBaselineHeaders(HttpResponseMessage res)
    {
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", res.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
        Assert.Equal(SecurityHeadersMiddleware.PermissionsPolicy, res.Headers.GetValues("Permissions-Policy").Single());
        Assert.True(res.Headers.Contains("Referrer-Policy"));
    }

    private static string Csp(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Content-Security-Policy", out var v) ? v.Single() : "";

    private string SpaRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helpdesk-spa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html><title>Helpdesk</title><div id=\"root\"></div>");
        File.WriteAllText(Path.Combine(dir, "assets", "index-abc123.js"), "console.log('app');");
        _tempDirs.Add(dir);
        return dir;
    }

    // ---- Security headers ----

    [Fact]
    public async Task ApiResponses_CarrySecurityHeaders_AndAreNeverCached()
    {
        using var f = new ApiFactory();
        var res = await Client(f).GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertBaselineHeaders(res);
        Assert.Equal(SecurityHeadersMiddleware.ApiContentSecurityPolicy, Csp(res));
        Assert.True(res.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task ErrorResponses_KeepSecurityHeaders()
    {
        using var f = new ApiFactory();
        f.Router.Handler = _ => throw new InvalidOperationException("boom");
        var res = await Client(f).SendAsync(Chat(ChatJson()));

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        await Problem(res);
        AssertBaselineHeaders(res);
        Assert.Equal(SecurityHeadersMiddleware.ApiContentSecurityPolicy, Csp(res));
    }

    [Fact]
    public async Task UnknownApiPath_IsA404Problem_NotTheSpa()
    {
        using var f = new ApiFactory(devAuth: false);
        var res = await Client(f).GetAsync("/api/v1/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await Problem(res);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        AssertBaselineHeaders(res);
    }

    // ---- Request body limit ----

    [Fact]
    public async Task BodyOverTheLimit_Is413Problem_AndNeverReachesTheRouter()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Http:MaxRequestBodyBytes"] = "20000" });
        var routed = false;
        f.Router.Handler = _ => { routed = true; return ("PUBLIC_TECH", ""); };
        var res = await Client(f).SendAsync(Chat(ChatJson(padTo: 20001)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        var body = await Problem(res);
        Assert.Equal("request_too_large", body.GetProperty("code").GetString());
        Assert.False(routed);
        AssertBaselineHeaders(res);
    }

    [Fact]
    public async Task BodyJustUnderTheDefaultLimit_IsAccepted()
    {
        using var f = new ApiFactory();
        var res = await Client(f).SendAsync(Chat(ChatJson(padTo: (int)HttpHardeningOptions.DefaultMaxRequestBodyBytes)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task BodyLimit_DoesNotApplyOutsideTheApi()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Http:MaxRequestBodyBytes"] = "20000" });
        var res = await Client(f).PostAsync("/healthz", new StringContent(new string('x', 30000)));

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public void DefaultLimit_FitsTheLargestValidChatRequest()
    {
        Assert.True(HttpHardeningOptions.DefaultMaxRequestBodyBytes >= HttpHardeningOptions.MinRequestBodyBytes);
        Assert.True(HttpHardeningOptions.DefaultMaxRequestBodyBytes <= HttpHardeningOptions.MaxAllowedRequestBodyBytes);
    }

    // ---- HTTPS, HSTS and forwarded headers ----

    [Fact]
    public async Task Production_Https_SendsHsts()
    {
        using var f = Production();
        var res = await Client(f).GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var hsts = res.Headers.GetValues("Strict-Transport-Security").Single();
        Assert.Contains("max-age=31536000", hsts);
        Assert.Contains("includeSubDomains", hsts);
        Assert.DoesNotContain("preload", hsts);
    }

    [Fact]
    public async Task Production_PlainHttp_IsRedirectedToHttps()
    {
        using var f = Production();
        var res = await Client(f, HttpBase).GetAsync("/api/v1/chat/whatever?x=1");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, res.StatusCode);
        Assert.Equal("https://helpdesk.example.com/api/v1/chat/whatever?x=1", res.Headers.Location?.ToString());
        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    public async Task Production_Probes_AreNotRedirected(string path)
    {
        using var f = Production();
        var res = await Client(f, HttpBase).GetAsync(path);

        Assert.NotEqual(HttpStatusCode.TemporaryRedirect, res.StatusCode);
    }

    [Fact]
    public async Task Production_RedirectCanBeTurnedOff()
    {
        using var f = Production(b => b.UseSetting("Http:HttpsRedirection", "false"));
        var res = await Client(f, HttpBase).GetAsync("/api/v1/unknown");

        Assert.NotEqual(HttpStatusCode.TemporaryRedirect, res.StatusCode);
    }

    [Fact]
    public async Task Development_DoesNotRedirectOrSendHsts()
    {
        using var f = WithClientIp(new ApiFactory());
        var res = await Client(f, HttpBase).GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task ForwardedProto_FromATrustedProxy_IsHonoured()
    {
        using var f = Production(b => b.UseSetting("Http:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/16"));
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(HttpBase, "/api/v1/unknown"));
        req.Headers.Add(RemoteIpStartupFilter.Header, "10.0.4.7");
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-For", "198.51.100.23");
        var res = await Client(f, HttpBase).SendAsync(req);

        Assert.NotEqual(HttpStatusCode.TemporaryRedirect, res.StatusCode);
        Assert.True(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task ForwardedProto_FromAnUntrustedAddress_IsIgnored()
    {
        using var f = Production(b => b.UseSetting("Http:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/16"));
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(HttpBase, "/api/v1/unknown"));
        req.Headers.Add(RemoteIpStartupFilter.Header, "203.0.113.9");
        req.Headers.Add("X-Forwarded-Proto", "https");
        var res = await Client(f, HttpBase).SendAsync(req);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, res.StatusCode);
    }

    [Fact]
    public async Task ForwardedProto_WithNoConfiguredProxies_IsIgnored()
    {
        using var f = Production();
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(HttpBase, "/api/v1/unknown"));
        req.Headers.Add(RemoteIpStartupFilter.Header, "10.0.4.7");
        req.Headers.Add("X-Forwarded-Proto", "https");
        var res = await Client(f, HttpBase).SendAsync(req);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, res.StatusCode);
    }

    // ---- SPA hosting ----

    [Fact]
    public async Task Spa_IsServedWithItsPolicy_AndIndexRevalidates()
    {
        using var f = new ApiFactory(devAuth: false).WithWebHostBuilder(b => b.UseWebRoot(SpaRoot()));
        var client = Client(f);

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
        Assert.True(root.Headers.CacheControl?.NoCache);
        AssertBaselineHeaders(root);
        var csp = Csp(root);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
    }

    [Fact]
    public async Task Spa_ClientRoutes_FallBackToIndex_WithoutSignIn()
    {
        using var f = new ApiFactory(devAuth: false).WithWebHostBuilder(b => b.UseWebRoot(SpaRoot()));
        var res = await Client(f).GetAsync("/tickets/42");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("<div id=\"root\">", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Spa_FingerprintedAssets_AreCachedForever()
    {
        using var f = new ApiFactory(devAuth: false).WithWebHostBuilder(b => b.UseWebRoot(SpaRoot()));
        var res = await Client(f).GetAsync("/assets/index-abc123.js");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Spa_MissingAsset_Is404_NotIndex()
    {
        using var f = new ApiFactory(devAuth: false).WithWebHostBuilder(b => b.UseWebRoot(SpaRoot()));
        var res = await Client(f).GetAsync("/assets/missing.js");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Spa_UnknownApiPath_StillReturnsAProblem()
    {
        using var f = new ApiFactory(devAuth: false).WithWebHostBuilder(b => b.UseWebRoot(SpaRoot()));
        var res = await Client(f).GetAsync("/api/v2/nothing");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("not_found", (await Problem(res)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Spa_Policy_AllowsTheSignInAuthority()
    {
        using var f = new ApiFactory(devAuth: false, testJwt: false, settings: new Dictionary<string, string>
        {
            ["Auth:Authority"] = "https://contoso.ciamlogin.com/tenant-id/v2.0",
        }).WithWebHostBuilder(b => b.UseWebRoot(SpaRoot()));
        var csp = Csp(await Client(f).GetAsync("/"));

        Assert.Contains("connect-src 'self' https://contoso.ciamlogin.com", csp);
        Assert.Contains("frame-src https://contoso.ciamlogin.com", csp);
    }

    [Fact]
    public async Task WithoutABuiltSpa_TheRootIsNotServed()
    {
        using var f = new ApiFactory(devAuth: false);
        var res = await Client(f).GetAsync("/");

        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- Configuration checks ----

    private static async Task<JsonElement> StatusCheck(WebApplicationFactory<Program> f, string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/status");
        req.Headers.Add("X-Dev-Tenant", "t1");
        req.Headers.Add("X-Dev-User", "u1");
        var raw = await (await Client(f).SendAsync(req)).Content.ReadAsStringAsync();
        return JsonDocument.Parse(raw).RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("area").GetString() == "Http" && c.GetProperty("name").GetString() == name).Clone();
    }

    [Theory]
    [InlineData("Http:ForwardedHeaders:KnownProxies:0", "not-an-ip", "ForwardedHeaders", "fail")]
    [InlineData("Http:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99", "ForwardedHeaders", "fail")]
    [InlineData("Http:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/16", "ForwardedHeaders", "pass")]
    [InlineData("Http:ForwardedHeaders:ForwardLimit", "0", "ForwardedHeaders", "fail")]
    [InlineData("Http:HttpsPort", "70000", "HttpsPort", "fail")]
    [InlineData("Http:Hsts:MaxAgeDays", "0", "Hsts", "fail")]
    [InlineData("Http:MaxRequestBodyBytes", "1024", "MaxRequestBodyBytes", "fail")]
    [InlineData("Http:MaxRequestBodyBytes", "8388608", "MaxRequestBodyBytes", "fail")]
    [InlineData("Http:MaxRequestBodyBytes", "32768", "MaxRequestBodyBytes", "warn")]
    [InlineData("Http:MaxRequestBodyBytes", "131072", "MaxRequestBodyBytes", "pass")]
    [InlineData("Http:Csp:AuthorityOrigins:0", "http://login.example.com", "Csp", "fail")]
    [InlineData("Http:Csp:AuthorityOrigins:0", "https://login.example.com/path", "Csp", "fail")]
    [InlineData("Http:Csp:AuthorityOrigins:0", "https://login.example.com", "Csp", "pass")]
    [InlineData("Http:Csp:Policy", "default-src 'self'", "Csp", "warn")]
    public async Task Validator_ChecksHttpSettings(string key, string value, string check, string expected)
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { [key] = value });
        var result = await StatusCheck(f, check);

        Assert.Equal(expected, result.GetProperty("status").GetString());
    }

    [Fact]
    public void Production_WithoutTrustedProxies_StillStarts()
    {
        using var f = Production();
        using var client = f.CreateClient();
    }

    [Fact]
    public void Production_WithAnInvalidProxy_RefusesToStart()
    {
        using var f = Production(b => b.UseSetting("Http:ForwardedHeaders:KnownProxies:0", "not-an-ip"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Http/ForwardedHeaders", ex.ToString());
    }
}
