using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Helpdesk.Infrastructure.Security;

namespace Helpdesk.Api.Tests;

public class IdentityRuleTests
{
    private static ClaimsPrincipal Principal(string? tenant, string? user)
    {
        var claims = new List<Claim>();
        if (tenant is not null) claims.Add(new(JwtTenantProvider.TenantClaim, tenant));
        if (user is not null) claims.Add(new(ClaimTypes.NameIdentifier, user));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData("tenant-1", "user-1", true)]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", "AAAAAAAAAAAAAAAA-bbbb_cccc", true)]
    [InlineData("a:b", "c", false)]
    [InlineData("a", "b:c", false)]
    [InlineData(":", "c", false)]
    [InlineData("a", ":", false)]
    [InlineData("", "c", false)]
    [InlineData("a", " ", false)]
    [InlineData(null, "c", false)]
    [InlineData("a", null, false)]
    public void HasValidIdentity(string? tenant, string? user, bool expected)
    {
        Assert.Equal(expected, JwtTenantProvider.HasValidIdentity(Principal(tenant, user)));
    }

    [Fact]
    public void ExtractionThrows_SoNoCallerCanUseAnAmbiguousId()
    {
        Assert.Throws<UnauthorizedAccessException>(() => JwtTenantProvider.TenantId(Principal("a:b", "c")));
        Assert.Throws<UnauthorizedAccessException>(() => JwtTenantProvider.UserId(Principal("a", "b:c")));
    }

    [Fact]
    public void TheTwoColliding_TenantUserPairs_CannotBothExist()
    {
        // These two would build the same composite key "a:b:c" if both were allowed.
        Assert.False(JwtTenantProvider.HasValidIdentity(Principal("a:b", "c")));
        Assert.False(JwtTenantProvider.HasValidIdentity(Principal("a", "b:c")));
    }

    private static HttpRequestMessage Chat(string tenant, string user)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/message")
        {
            Content = JsonContent.Create(new { messageText = "excel xlookup", conversationId = Guid.NewGuid() })
        };
        req.Headers.Add("X-Dev-Tenant", tenant);
        req.Headers.Add("X-Dev-User", user);
        return req;
    }

    [Theory]
    [InlineData("a:b", "user-1")]
    [InlineData("tenant-1", "c:d")]
    public async Task Rest_RefusesAnIdWithAColon_With403_AndNeverReachesThePipeline(string tenant, string user)
    {
        using var f = new ApiFactory();
        var res = await f.CreateClient().SendAsync(Chat(tenant, user));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(f.Publisher.Sent);
    }

    [Fact]
    public async Task Rest_AcceptsOrdinaryIds()
    {
        using var f = new ApiFactory();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().SendAsync(Chat("tenant-1", "user-1"))).StatusCode);
    }

    [Fact]
    public async Task Status_AlsoRefusesAnIdWithAColon()
    {
        using var f = new ApiFactory();
        var req = new HttpRequestMessage(HttpMethod.Get, "/status");
        req.Headers.Add("X-Dev-Tenant", "a:b");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.CreateClient().SendAsync(req)).StatusCode);
    }
}
