using System.Net.Http.Json;
using System.Text.Json;

namespace Helpdesk.Api.Tests;

public class CorrelationIdTests
{
    private const string Header = "X-Correlation-ID";

    private static HttpRequestMessage Chat(string? correlationId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/message")
        {
            Content = JsonContent.Create(new { messageText = "excel xlookup", conversationId = Guid.NewGuid() })
        };
        if (correlationId is not null) req.Headers.Add(Header, correlationId);
        return req;
    }

    [Fact]
    public async Task SuppliedId_IsEchoedOnTheResponse()
    {
        using var f = new ApiFactory();
        var res = await f.CreateClient().SendAsync(Chat("support-ticket-4711"));
        Assert.Equal("support-ticket-4711", res.Headers.GetValues(Header).Single());
    }

    [Fact]
    public async Task MissingId_IsGenerated()
    {
        using var f = new ApiFactory();
        var res = await f.CreateClient().SendAsync(Chat());
        Assert.Matches("^[0-9a-f]{32}$", res.Headers.GetValues(Header).Single());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces and $ymbols!!")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task MalformedId_IsReplaced(string supplied)
    {
        using var f = new ApiFactory();
        var req = Chat();
        req.Headers.TryAddWithoutValidation(Header, supplied);
        var res = await f.CreateClient().SendAsync(req);
        Assert.NotEqual(supplied, res.Headers.GetValues(Header).Single());
    }

    [Fact]
    public async Task ErrorResponse_CarriesTheCorrelationId()
    {
        using var f = new ApiFactory();
        f.Router.Handler = _ => ("NOT_A_LANE", "");
        var res = await f.CreateClient().SendAsync(Chat("support-ticket-4711"));

        Assert.False(res.IsSuccessStatusCode);
        var problem = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("support-ticket-4711", problem.GetProperty("correlationId").GetString());
    }
}
