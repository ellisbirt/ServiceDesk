using System.Net.Http.Json;
using System.Threading.Channels;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Helpdesk.Api.Tests;

/// <summary>A connected hub client that records every <c>chatEvent</c> it receives.</summary>
internal sealed class HubClient : IAsyncDisposable
{
    private readonly Channel<ChatStreamEvent> _events = Channel.CreateUnbounded<ChatStreamEvent>();

    public HubConnection Connection { get; }
    public List<ChatStreamEvent> Seen { get; } = [];

    private HubClient(HubConnection connection)
    {
        Connection = connection;
        connection.On<ChatStreamEvent>("chatEvent", e => _events.Writer.TryWrite(e));
    }

    public static async Task<HubClient> ConnectAsync(
        ApiFactory factory, string tenant = "tenant-a", string user = "user-1", string? userAgent = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling; // TestServer has no real socket
                o.Headers["X-Dev-Tenant"] = tenant;
                o.Headers["X-Dev-User"] = user;
                if (userAgent is not null) o.Headers["User-Agent"] = userAgent;
            })
            .Build();
        var client = new HubClient(connection);
        await connection.StartAsync();
        return client;
    }

    public static async Task<string> MintTicketAsync(ApiFactory factory, string jwt)
    {
        using var http = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/ticket");
        req.Headers.Authorization = new("Bearer", jwt);
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("ticket").GetString()!;
    }

    /// <summary>
    /// Connects the way the browser does with real authentication: the access token is exchanged over HTTP for a
    /// ticket, and only the ticket goes to the hub (in the header for negotiate, in the URL for the WebSocket).
    /// <paramref name="credential"/> overrides what is presented, to test a raw token or a bad ticket.
    /// </summary>
    public static async Task<HubClient> ConnectWithTicketAsync(
        ApiFactory factory, string jwt, List<Uri>? webSocketUrls = null, Func<Task<string?>>? credential = null,
        List<string>? issuedTickets = null)
    {
        async Task<string?> Mint()
        {
            var ticket = await MintTicketAsync(factory, jwt);
            issuedTickets?.Add(ticket);
            return ticket;
        }

        var provider = credential ?? Mint;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.WebSockets;
                o.WebSocketFactory = async (context, ct) =>
                {
                    // The browser's SignalR client asks for a credential again when it opens the WebSocket and
                    // puts it in the URL (it cannot set a header). The .NET client would use a header instead, so
                    // do what the browser does, to test the real path.
                    var url = new UriBuilder(context.Uri);
                    if (await provider() is { } token) url.Query = $"{url.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}";
                    webSocketUrls?.Add(url.Uri);
                    return await factory.Server.CreateWebSocketClient().ConnectAsync(url.Uri, ct);
                };
                o.AccessTokenProvider = provider;
            })
            .Build();
        var client = new HubClient(connection);
        await connection.StartAsync();
        return client;
    }

    public Task<ChatAck> SendAsync(string text, Guid? conversationId = null) =>
        Connection.InvokeAsync<ChatAck>("Send", new ChatMessageRequest
        {
            MessageText = text,
            ConversationId = conversationId ?? Guid.NewGuid()
        });

    /// <summary>Uploads the request as a stream of parts: the message first, then one history message per part.</summary>
    public async Task<ChatAck> SendStreamedAsync(
        string text, IReadOnlyList<ChatHistoryMessage> history, Guid? conversationId = null, int extraParts = 0)
    {
        var parts = Channel.CreateUnbounded<ChatRequestPart>();
        parts.Writer.TryWrite(new ChatRequestPart { ConversationId = conversationId ?? Guid.NewGuid(), MessageText = text });
        foreach (var message in history) parts.Writer.TryWrite(new ChatRequestPart { History = message });
        for (var i = 0; i < extraParts; i++) parts.Writer.TryWrite(new ChatRequestPart { History = history[0] });
        parts.Writer.Complete();
        return await Connection.InvokeAsync<ChatAck>("SendStreamed", parts.Reader);
    }

    /// <summary>Collects events until a completed or failed one arrives.</summary>
    public async Task<List<ChatStreamEvent>> UntilTerminalAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var received = new List<ChatStreamEvent>();
        await foreach (var e in _events.Reader.ReadAllAsync(cts.Token))
        {
            received.Add(e);
            if (e.Type is ChatEventTypes.Completed or ChatEventTypes.Failed) break;
        }
        return received;
    }

    public async Task<bool> ReceivesAnythingAsync(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try
        {
            await _events.Reader.WaitToReadAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

public class ChatHubTests
{
    [Fact]
    public async Task UserAgent_IsRetainedForQueuedModelContext()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f, userAgent: "HubBrowser/4.2");

        var ack = await client.SendAsync("how do I fix excel");
        var events = await client.UntilTerminalAsync();

        Assert.NotNull(ack.RequestId);
        Assert.Equal(ChatEventTypes.Completed, events[^1].Type);
        Assert.Equal("HubBrowser/4.2", f.Router.LastUserAgent);
        Assert.Equal("HubBrowser/4.2", f.Inference.LastUserAgent);
    }

    [Fact]
    public async Task Send_ReturnsImmediately_ThenStreamsAndCompletes()
    {
        using var f = new ApiFactory();
        f.Inference.OnReply = async (progress, ct) =>
        {
            await progress!.OnTextAsync("Hello ", ct);
            await Task.Delay(80, ct); // beyond the flush interval so the two deltas are separate messages
            await progress.OnTextAsync("world", ct);
        };
        await using var client = await HubClient.ConnectAsync(f);

        var ack = await client.SendAsync("how do I fix excel");
        var events = await client.UntilTerminalAsync();

        Assert.Equal(ChatEventTypes.Started, events[0].Type);
        Assert.NotNull(ack.RequestId);
        Assert.All(events, e => Assert.Equal(ack.RequestId, e.RequestId));
        Assert.Equal("Hello world", string.Concat(events.Where(e => e.Type == ChatEventTypes.Delta).Select(e => e.Text)));
        var done = events[^1];
        Assert.Equal(ChatEventTypes.Completed, done.Type);
        Assert.Equal("tech answer", done.Response!.ResponseText);
        Assert.Equal(ActionLanes.PublicTech, done.Response.ActionLane);
    }

    [Theory]
    [InlineData("a")]       // plain text: about 84 KB in total, 4 KB per part
    [InlineData("é")]  // non-ASCII: escaped to six bytes per character by the .NET client
    [InlineData("\"")]      // quotes: escaped in JSON
    public async Task LargestAllowedRequest_SentInParts_IsAcceptedWithoutRaisingTheMessageLimit(string unit)
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);
        var text = string.Concat(Enumerable.Repeat(unit, Helpdesk.Api.Chat.ChatRequestValidator.MaxMessageLength));
        var conversationId = Guid.NewGuid();
        var history = f.SignedHistory("tenant-a", "user-1", conversationId, Enumerable
            .Range(0, Helpdesk.Api.Chat.ChatRequestValidator.MaxHistoryMessages)
            .Select(i => (i % 2 == 0 ? "user" : "assistant", text))
            .ToArray());

        var ack = await client.SendStreamedAsync(text, history, conversationId);

        Assert.NotNull(ack.RequestId);
        Assert.Equal(ChatEventTypes.Completed, (await client.UntilTerminalAsync())[^1].Type);
        Assert.Equal(history.Count, f.Router.LastHistory!.Count); // every signature survived the trip in parts
    }

    [Fact]
    public async Task OneWholeRequest_LargerThanTheMessageLimit_ClosesTheConnection_WhichIsWhyTheClientSplitsIt()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);
        var text = new string('a', Helpdesk.Api.Chat.ChatRequestValidator.MaxMessageLength);
        var history = Enumerable.Range(0, 10).Select(_ => new ChatHistoryMessage { Role = "user", Content = text }).ToList();

        await Assert.ThrowsAnyAsync<Exception>(() => client.Connection.InvokeAsync<ChatAck>("Send", new ChatMessageRequest
        {
            MessageText = text,
            ConversationId = Guid.NewGuid(),
            ConversationHistory = history
        }));
    }

    [Fact]
    public async Task StreamedParts_ArriveInOrder_AndMatchWhatSendWouldHaveDelivered()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);
        var conversationId = Guid.NewGuid();
        var history = f.SignedHistory("tenant-a", "user-1", conversationId, Enumerable.Range(0, 5)
            .Select(i => (i % 2 == 0 ? "user" : "assistant", $"message {i}"))
            .ToArray());

        var ack = await client.SendStreamedAsync("latest question", history, conversationId);
        await client.UntilTerminalAsync();

        Assert.NotNull(ack.RequestId);
        Assert.Equal(history.Select(m => m.Content), f.Router.LastHistory!.Select(m => m.Content));
    }

    [Fact]
    public async Task StreamedRequest_WithTooManyHistoryMessages_IsRejected()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);
        var history = Enumerable.Range(0, Helpdesk.Api.Chat.ChatRequestValidator.MaxHistoryMessages)
            .Select(_ => new ChatHistoryMessage { Role = "user", Content = "hi" }).ToList();

        var ack = await client.SendStreamedAsync("question", history, extraParts: 1);

        Assert.Equal("invalid_request", ack.ErrorCode);
        Assert.Null(ack.RequestId);
        Assert.False(await client.ReceivesAnythingAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task StreamedRequest_WithAMalformedFirstPart_IsRejected()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);
        var parts = Channel.CreateUnbounded<ChatRequestPart>();
        parts.Writer.TryWrite(new ChatRequestPart { History = new ChatHistoryMessage { Role = "user", Content = "no header" } });
        parts.Writer.Complete();

        var ack = await client.Connection.InvokeAsync<ChatAck>("SendStreamed", parts.Reader);

        Assert.Equal("invalid_request", ack.ErrorCode);
    }

    [Fact]
    public async Task StreamedRequest_WithNoParts_IsRejected()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);
        var parts = Channel.CreateUnbounded<ChatRequestPart>();
        parts.Writer.Complete();

        Assert.Equal("invalid_request", (await client.Connection.InvokeAsync<ChatAck>("SendStreamed", parts.Reader)).ErrorCode);
    }

    [Fact]
    public async Task StreamedRequest_GoesThroughTheSameValidationAsSend()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);

        var ack = await client.SendStreamedAsync(
            new string('x', Helpdesk.Api.Chat.ChatRequestValidator.MaxMessageLength + 1), []);

        Assert.Equal("invalid_request", ack.ErrorCode);
    }

    [Fact]
    public async Task Hub_RefusesATenantOrUserIdContainingAColon()
    {
        using var f = new ApiFactory();
        await Assert.ThrowsAnyAsync<Exception>(() => HubClient.ConnectAsync(f, tenant: "a:b"));
        await Assert.ThrowsAnyAsync<Exception>(() => HubClient.ConnectAsync(f, user: "c:d"));
        await using var ok = await HubClient.ConnectAsync(f, tenant: "a-b", user: "c-d");
    }

    [Fact]
    public async Task Heartbeats_ArriveWhileTheJobIsStillRunning()
    {
        using var f = new ApiFactory(heartbeatSeconds: 1);
        f.Inference.OnReply = (_, ct) => Task.Delay(TimeSpan.FromSeconds(2.5), ct);
        await using var client = await HubClient.ConnectAsync(f);

        await client.SendAsync("slow question");
        var events = await client.UntilTerminalAsync(TimeSpan.FromSeconds(15));

        var beats = events.Where(e => e.Type == ChatEventTypes.Heartbeat).ToList();
        Assert.True(beats.Count >= 2, $"expected at least 2 heartbeats, saw {beats.Count}");
        Assert.True(beats[0].ElapsedSeconds >= 1);
        Assert.Equal(ChatEventTypes.Completed, events[^1].Type);
    }

    [Fact]
    public async Task RateLimitWait_IsReportedToTheClient()
    {
        using var f = new ApiFactory();
        f.Inference.OnReply = async (progress, ct) =>
            await progress!.OnRateLimitedAsync(TimeSpan.FromSeconds(42), attempt: 1, ct);
        await using var client = await HubClient.ConnectAsync(f);

        await client.SendAsync("question");
        var events = await client.UntilTerminalAsync();

        var limited = Assert.Single(events, e => e.Type == ChatEventTypes.RateLimited);
        Assert.Equal(42, limited.RetryAfterSeconds);
        Assert.Equal(1, limited.Attempt);
    }

    [Fact]
    public async Task ExhaustedRateLimit_FailsWithAStableCode()
    {
        using var f = new ApiFactory();
        f.Inference.OnReply = (_, _) => throw new UpstreamRateLimitedException(TimeSpan.FromMinutes(5), "limit");
        await using var client = await HubClient.ConnectAsync(f);

        await client.SendAsync("question");
        var events = await client.UntilTerminalAsync();

        var failed = events[^1];
        Assert.Equal(ChatEventTypes.Failed, failed.Type);
        Assert.Equal("rate_limited", failed.ErrorCode);
    }

    [Fact]
    public async Task ClassifierFailure_FailsClosed_AndNothingIsQueued()
    {
        using var f = new ApiFactory();
        f.Router.Handler = _ => throw new GuardrailClassificationException("nope");
        await using var client = await HubClient.ConnectAsync(f);

        await client.SendAsync("anything");
        var events = await client.UntilTerminalAsync();

        Assert.Equal("classification_failed", events[^1].ErrorCode);
        Assert.Empty(f.Publisher.Sent);
        Assert.DoesNotContain(events, e => e.Type == ChatEventTypes.Delta);
    }

    [Fact]
    public async Task PrivateCorp_WaitsForTheForm_WithATokenBoundToTheConnectionsTenantAndUser()
    {
        using var f = new ApiFactory();
        f.Router.Handler = _ => (ActionLanes.PrivateCorp, "");
        await using var client = await HubClient.ConnectAsync(f, tenant: "tenant-z", user: "alice");

        await client.SendAsync("vpn is down");
        var events = await client.UntilTerminalAsync();

        Assert.Equal(ChatEventTypes.Completed, events[^1].Type);
        Assert.Empty(f.Publisher.Sent);
        var reply = events[^1].Response!;
        Assert.NotNull(reply.IntakeForm);
        Assert.Equal("vpn is down", f.PendingEscalation("tenant-z", "alice", reply)!.Payload);
        Assert.Null(f.PendingEscalation("tenant-z", "bob", reply));
    }

    [Fact]
    public async Task SecondMessage_InTheSameConversation_IsRejectedWhileOneIsRunning()
    {
        using var f = new ApiFactory();
        var release = new TaskCompletionSource();
        f.Inference.OnReply = (_, _) => release.Task;
        await using var client = await HubClient.ConnectAsync(f);
        var conversation = Guid.NewGuid();

        await client.SendAsync("first", conversation);
        var rejected = await client.SendAsync("second", conversation);
        Assert.Equal("in_progress", rejected.ErrorCode);
        Assert.Null(rejected.RequestId);

        release.SetResult();
        Assert.Equal(ChatEventTypes.Completed, (await client.UntilTerminalAsync())[^1].Type);

        // The conversation is free again once the job has finished.
        await client.SendAsync("third", conversation);
        Assert.Equal(ChatEventTypes.Completed, (await client.UntilTerminalAsync())[^1].Type);
    }

    [Fact]
    public async Task InvalidRequest_IsRejectedBeforeAnythingIsQueued()
    {
        using var f = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(f);

        var ack = await client.SendAsync("   ");

        Assert.Equal("invalid_request", ack.ErrorCode);
        Assert.Null(ack.RequestId);
        Assert.Empty(f.Publisher.Sent);
    }

    [Fact]
    public async Task Events_AreOnlyDeliveredToTheSameTenantAndUser()
    {
        using var f = new ApiFactory();
        await using var sender = await HubClient.ConnectAsync(f, "tenant-a", "user-1");
        await using var sameUserOtherTab = await HubClient.ConnectAsync(f, "tenant-a", "user-1");
        await using var otherUser = await HubClient.ConnectAsync(f, "tenant-a", "user-2");
        await using var otherTenant = await HubClient.ConnectAsync(f, "tenant-b", "user-1");

        await sender.SendAsync("hello");
        await sender.UntilTerminalAsync();

        Assert.Equal(ChatEventTypes.Completed, (await sameUserOtherTab.UntilTerminalAsync())[^1].Type);
        Assert.False(await otherUser.ReceivesAnythingAsync(TimeSpan.FromMilliseconds(300)));
        Assert.False(await otherTenant.ReceivesAnythingAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Hub_RequiresAuthentication()
    {
        using var f = new ApiFactory(devAuth: false);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        await connection.DisposeAsync();
    }
}
