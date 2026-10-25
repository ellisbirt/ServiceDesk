using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Api.Security;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The ticket store on its own, with a clock the test controls.</summary>
public class HubTicketStoreTests
{
    private readonly ManualTime _time = new();

    private InMemoryHubTicketStore Store(int seconds = 30, int perUser = 10, int total = 100) =>
        new(Options.Create(new HubTicketOptions { TicketSeconds = seconds, MaxTicketsPerUser = perUser, MaxTickets = total }), _time);

    private HubTicketClaims Claims(string tenant = "t1", string user = "u1", int tokenMinutes = 60) =>
        new(tenant, user, _time.GetUtcNow().AddMinutes(tokenMinutes));

    [Fact]
    public void ATicket_CanBeRedeemedOnce()
    {
        var store = Store();
        var ticket = store.Issue(Claims())!;

        var first = store.Redeem(ticket.Value);
        Assert.Equal(("t1", "u1"), (first!.TenantId, first.UserId));
        Assert.Null(store.Redeem(ticket.Value));
    }

    [Fact]
    public void AnExpiredTicket_IsRefused()
    {
        var store = Store(seconds: 30);
        var ticket = store.Issue(Claims())!;

        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Null(store.Redeem(ticket.Value));
    }

    [Fact]
    public void AnUnknownOrEmptyTicket_IsRefused()
    {
        var store = Store();
        store.Issue(Claims());

        Assert.Null(store.Redeem("not-a-ticket"));
        Assert.Null(store.Redeem(""));
    }

    [Fact]
    public void TicketsAreUnguessable_AndDistinct()
    {
        var store = Store();
        var values = Enumerable.Range(0, 5).Select(i => store.Issue(Claims(user: $"u{i}"))!.Value).ToList();

        Assert.Equal(5, values.Distinct().Count());
        Assert.All(values, v => Assert.True(v.Length >= 43)); // 256 bits, base64url
    }

    [Fact]
    public void ALifetime_IsCappedByTheAccessTokensOwnExpiry()
    {
        var store = Store(seconds: 30);
        var ticket = store.Issue(new HubTicketClaims("t1", "u1", _time.GetUtcNow().AddSeconds(10)))!;

        Assert.Equal(TimeSpan.FromSeconds(10), ticket.Lifetime);
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(store.Redeem(ticket.Value));
    }

    [Fact]
    public void NoTicket_IsIssuedForAnAlreadyExpiredToken()
    {
        Assert.Null(Store().Issue(new HubTicketClaims("t1", "u1", _time.GetUtcNow().AddSeconds(-1))));
    }

    [Fact]
    public void TheClaimsReturned_AreTheOnesIssuedFor_NotAnotherUsers()
    {
        var store = Store();
        var alice = store.Issue(Claims("t1", "alice"))!;
        var bob = store.Issue(Claims("t2", "bob"))!;

        Assert.Equal("bob", store.Redeem(bob.Value)!.UserId);
        Assert.Equal("alice", store.Redeem(alice.Value)!.UserId);
    }

    [Fact]
    public void AUser_CannotHoldMoreThanTheirShare_ButOthersAreUnaffected()
    {
        var store = Store(perUser: 2);
        Assert.NotNull(store.Issue(Claims(user: "greedy")));
        Assert.NotNull(store.Issue(Claims(user: "greedy")));

        Assert.Null(store.Issue(Claims(user: "greedy")));
        Assert.NotNull(store.Issue(Claims(user: "other")));
    }

    [Fact]
    public void UsedAndExpiredTickets_FreeTheirPlace()
    {
        var store = Store(perUser: 2);
        var first = store.Issue(Claims())!;
        store.Issue(Claims());
        Assert.Null(store.Issue(Claims()));

        store.Redeem(first.Value);
        Assert.NotNull(store.Issue(Claims()));

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.NotNull(store.Issue(Claims()));
        Assert.NotNull(store.Issue(Claims()));
    }

    [Fact]
    public void TheStore_HasAnOverallLimit()
    {
        var store = Store(perUser: 100, total: 3);
        for (var i = 0; i < 3; i++) Assert.NotNull(store.Issue(Claims(user: $"u{i}")));

        Assert.Null(store.Issue(Claims(user: "one-too-many")));
    }

    [Fact]
    public void TwoSimultaneRedemptions_CannotBothSucceed()
    {
        var store = Store();
        for (var round = 0; round < 200; round++)
        {
            var ticket = store.Issue(Claims(user: $"u{round}"))!;
            var wins = 0;
            Parallel.For(0, 8, _ =>
            {
                if (store.Redeem(ticket.Value) is not null) Interlocked.Increment(ref wins);
            });
            Assert.Equal(1, wins);
        }
    }
}

/// <summary>The exchange and the hub, with real JWT validation in front.</summary>
public class HubTicketFlowTests
{
    private static ApiFactory Factory(Dictionary<string, string>? settings = null) =>
        new(devAuth: false, settings: settings, testJwt: true);

    private static HttpRequestMessage Mint(string? jwt)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/ticket");
        if (jwt is not null) req.Headers.Authorization = new("Bearer", jwt);
        return req;
    }

    // ----- Minting -----

    [Fact]
    public async Task Mint_RequiresAnAccessToken()
    {
        using var f = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().SendAsync(Mint(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().SendAsync(Mint("not.a.jwt"))).StatusCode);
    }

    [Fact]
    public async Task Mint_ReturnsAShortLivedTicket_ThatMustNotBeCached()
    {
        using var f = Factory();
        var res = await f.CreateClient().SendAsync(Mint(ApiFactory.Jwt()));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("ticket").GetString()!.Length >= 43);
        Assert.InRange(body.GetProperty("expiresInSeconds").GetInt32(), 1, 30);
    }

    [Fact]
    public async Task Mint_LifetimeIsCappedByTheAccessTokenExpiry()
    {
        using var f = Factory();
        var res = await f.CreateClient().SendAsync(Mint(ApiFactory.Jwt(lifetime: TimeSpan.FromSeconds(10))));

        var seconds = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("expiresInSeconds").GetInt32();
        Assert.InRange(seconds, 1, 10);
    }

    [Fact]
    public async Task Mint_RefusesAnIdWithAColon()
    {
        using var f = Factory();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.CreateClient().SendAsync(Mint(ApiFactory.Jwt(tenant: "a:b")))).StatusCode);
    }

    [Fact]
    public async Task Mint_IsLimitedPerUser()
    {
        using var f = Factory(new() { ["Hub:MaxTicketsPerUser"] = "2" });
        var client = f.CreateClient();
        var jwt = ApiFactory.Jwt();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Mint(jwt))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Mint(jwt))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Mint(jwt))).StatusCode);
        // Another user is not affected.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Mint(ApiFactory.Jwt(user: "someone-else")))).StatusCode);
    }

    [Fact]
    public async Task Mint_HasItsOwnRateLimit()
    {
        using var f = Factory(new() { ["RateLimit:TicketPerMinute"] = "2", ["Hub:MaxTicketsPerUser"] = "100" });
        var client = f.CreateClient();
        var jwt = ApiFactory.Jwt();

        await client.SendAsync(Mint(jwt));
        await client.SendAsync(Mint(jwt));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Mint(jwt))).StatusCode);
    }

    [Fact]
    public async Task DevAuth_DoesNotUseTickets_SoThereIsNoMintEndpoint()
    {
        using var f = new ApiFactory(); // development header authentication
        var req = Mint(null);
        req.Headers.Add("X-Dev-Tenant", "t");
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateClient().SendAsync(req)).StatusCode);
    }

    // ----- The API still takes the JWT, and only the JWT -----

    [Fact]
    public async Task Rest_StillAcceptsTheAccessToken_ButNotATicket()
    {
        using var f = Factory();
        var client = f.CreateClient();
        HttpRequestMessage Chat(string bearer)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/message")
            {
                Content = JsonContent.Create(new { messageText = "excel xlookup", conversationId = Guid.NewGuid() })
            };
            req.Headers.Authorization = new("Bearer", bearer);
            return req;
        }

        var jwt = ApiFactory.Jwt();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Chat(jwt))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Chat(await HubClient.MintTicketAsync(f, jwt)))).StatusCode);
    }

    // ----- The hub -----

    [Fact]
    public async Task Hub_WorksWithATicket_AndKnowsWhoTheCallerIs()
    {
        using var f = Factory();
        f.Router.Handler = _ => (ActionLanes.PrivateCorp, "");
        await using var client = await HubClient.ConnectWithTicketAsync(f, ApiFactory.Jwt(tenant: "tenant-z", user: "alice"));

        var events = await client.SendAndConfirmAsync("VPN is down");

        Assert.Equal(ChatEventTypes.Completed, events[^1].Type);
        var evt = Assert.Single(f.Publisher.Sent);
        Assert.Equal("tenant-z", evt.TenantId);
        Assert.Equal("alice", evt.UserId);
    }

    [Fact]
    public async Task Hub_RefusesARawAccessToken_SoTheJwtCanNeverBeTheUrlCredential()
    {
        using var f = Factory();
        var jwt = ApiFactory.Jwt();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            HubClient.ConnectWithTicketAsync(f, jwt, credential: () => Task.FromResult<string?>(jwt)));
    }

    [Fact]
    public async Task Hub_RefusesATicketThatWasAlreadyUsed()
    {
        using var f = Factory();
        var jwt = ApiFactory.Jwt();
        var spent = await HubClient.MintTicketAsync(f, jwt);

        // Every request presents the same ticket: negotiate spends it, and the WebSocket then finds it used.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            HubClient.ConnectWithTicketAsync(f, jwt, credential: () => Task.FromResult<string?>(spent)));
    }

    [Fact]
    public async Task Hub_RefusesAMadeUpTicket_AndNoCredentialAtAll()
    {
        using var f = Factory();
        var jwt = ApiFactory.Jwt();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            HubClient.ConnectWithTicketAsync(f, jwt, credential: () => Task.FromResult<string?>("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            HubClient.ConnectWithTicketAsync(f, jwt, credential: () => Task.FromResult<string?>(null)));
    }

    [Fact]
    public async Task TheUrlCarriesATicket_NeverTheAccessToken()
    {
        using var f = Factory();
        var jwt = ApiFactory.Jwt();
        var urls = new List<Uri>();
        var tickets = new List<string>();

        await using var client = await HubClient.ConnectWithTicketAsync(f, jwt, urls, issuedTickets: tickets);

        var url = Assert.Single(urls).ToString();
        Assert.DoesNotContain(jwt, url);
        Assert.Contains("access_token=", url);
        Assert.Contains(tickets.Last(), url); // the WebSocket leg carries the second ticket
        Assert.Equal(2, tickets.Count);       // one for negotiate (header), one for the WebSocket (URL)
    }

    [Fact]
    public async Task TheConnection_ClosesWhenTheAccessTokenExpires()
    {
        using var f = Factory();
        await using var client = await HubClient.ConnectWithTicketAsync(f, ApiFactory.Jwt(lifetime: TimeSpan.FromSeconds(4)));
        var closed = new TaskCompletionSource();
        client.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(closed.Task, finished);
    }

    [Fact]
    public async Task NeitherTheAccessTokenNorATicket_AppearsInAnyLog()
    {
        using var f = Factory();
        var jwt = ApiFactory.Jwt();
        var tickets = new List<string>();
        await using (var client = await HubClient.ConnectWithTicketAsync(f, jwt, issuedTickets: tickets))
        {
            await client.SendAsync("how do I fix excel");
            await client.UntilTerminalAsync();
        }
        await f.CreateClient().SendAsync(Mint(jwt));

        var logs = f.Logs.Lines;
        Assert.NotEmpty(logs);
        Assert.DoesNotContain(logs, line => line.Contains(jwt));
        Assert.DoesNotContain(logs, line => tickets.Any(line.Contains));
        Assert.DoesNotContain(logs, line => line.Contains("access_token"));
    }

    [Fact]
    public async Task HubResponses_AreNotCacheable_AndSendNoReferrer()
    {
        using var f = new ApiFactory();
        var req = new HttpRequestMessage(HttpMethod.Post, "/hubs/chat/negotiate?negotiateVersion=1");
        req.Headers.Add("X-Dev-Tenant", "t");
        var res = await f.CreateClient().SendAsync(req);

        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
    }
}
