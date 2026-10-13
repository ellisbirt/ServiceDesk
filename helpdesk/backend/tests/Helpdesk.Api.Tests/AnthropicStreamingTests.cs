using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

public class AnthropicRateLimitTests
{
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;
        public int Calls => _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responses[Math.Min(_next++, responses.Length - 1)]());
    }

    private sealed class Progress : IChatProgress
    {
        public List<string> Text { get; } = [];
        public List<(TimeSpan Wait, int Attempt)> Waits { get; } = [];
        public ValueTask OnTextAsync(string delta, CancellationToken ct) { Text.Add(delta); return ValueTask.CompletedTask; }
        public ValueTask OnRateLimitedAsync(TimeSpan retryAfter, int attempt, CancellationToken ct)
        {
            Waits.Add((retryAfter, attempt));
            return ValueTask.CompletedTask;
        }
    }

    private static HttpResponseMessage TooMany(string? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"stop_reason":"end_turn","content":[{"type":"text","text":"hi"}]}""", Encoding.UTF8, "application/json")
    };

    private static AnthropicClient Client(HttpMessageHandler h, AnthropicOptions? o = null) =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://example.test/") },
            Options.Create(o ?? new AnthropicOptions { Model = "m" }), NullLogger<AnthropicClient>.Instance);

    [Fact]
    public async Task Retries_AfterRetryAfterHeader_AndReportsTheWait()
    {
        var handler = new ScriptedHandler(() => TooMany("0"), Ok);
        var progress = new Progress();

        var text = await Client(handler).CompleteAsync("sys", "user", 0.0, 100, progress: progress);

        Assert.Equal("hi", text);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, Assert.Single(progress.Waits).Attempt);
    }

    [Fact]
    public async Task WaitLongerThanTheCap_FailsImmediately_WithRetryAfter()
    {
        var handler = new ScriptedHandler(() => TooMany("300"));
        var client = Client(handler, new AnthropicOptions { Model = "m", RateLimitMaxWaitSeconds = 120 });

        var ex = await Assert.ThrowsAsync<UpstreamRateLimitedException>(() => client.CompleteAsync("sys", "user", 0.0, 100));

        Assert.Equal(TimeSpan.FromSeconds(300), ex.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GivesUp_AfterMaxRetries()
    {
        var handler = new ScriptedHandler(() => TooMany("0"));
        var client = Client(handler, new AnthropicOptions { Model = "m", RateLimitMaxRetries = 2 });

        await Assert.ThrowsAsync<UpstreamRateLimitedException>(() => client.CompleteAsync("sys", "user", 0.0, 100));

        Assert.Equal(3, handler.Calls); // first attempt + 2 retries
    }

    [Fact]
    public async Task MissingHeader_FallsBackToBackoff()
    {
        var handler = new ScriptedHandler(() => TooMany(null), Ok);
        var progress = new Progress();

        await Client(handler).CompleteAsync("sys", "user", 0.0, 100, progress: progress);

        Assert.True(progress.Waits.Single().Wait >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OtherErrors_AreNotRetriedHere()
    {
        var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).CompleteAsync("sys", "user", 0.0, 100));

        Assert.Equal(1, handler.Calls);
    }
}

public class AnthropicStreamingTests
{
    private sealed class SseHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _next;
        public List<JsonNode> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodies[Math.Min(_next++, bodies.Length - 1)], Encoding.UTF8, "text/event-stream")
            };
        }
    }

    /// <summary>A response whose body sends one event and then goes silent forever.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var pipe = new System.IO.Pipelines.Pipe();
            var first = Encoding.UTF8.GetBytes("event: ping\ndata: {\"type\":\"ping\"}\n\n");
            pipe.Writer.WriteAsync(first, ct).AsTask().Wait(ct);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(pipe.Reader.AsStream())
            });
        }
    }

    private sealed class Progress : IChatProgress
    {
        public List<string> Text { get; } = [];
        public ValueTask OnTextAsync(string delta, CancellationToken ct) { Text.Add(delta); return ValueTask.CompletedTask; }
        public ValueTask OnRateLimitedAsync(TimeSpan retryAfter, int attempt, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private static string Sse(params (string Type, string Json)[] events) =>
        string.Concat(events.Select(e => $"event: {e.Type}\ndata: {e.Json}\n\n"));

    private static (string, string) Start() => ("message_start", """{"type":"message_start","message":{"id":"m","content":[]}}""");
    private static (string, string) Stop(string reason) => ("message_delta", $$$"""{"type":"message_delta","delta":{"stop_reason":"{{{reason}}}"}}""");
    private static (string, string) End() => ("message_stop", """{"type":"message_stop"}""");
    private static (string, string) TextStart(int i) =>
        ("content_block_start", $$$"""{"type":"content_block_start","index":{{{i}}},"content_block":{"type":"text","text":""}}""");
    private static (string, string) TextDelta(int i, string t) =>
        ("content_block_delta", $$$"""{"type":"content_block_delta","index":{{{i}}},"delta":{"type":"text_delta","text":"{{{t}}}"}}""");
    private static (string, string) BlockStop(int i) =>
        ("content_block_stop", $$"""{"type":"content_block_stop","index":{{i}}}""");

    private static AnthropicClient Client(HttpMessageHandler h, AnthropicOptions? o = null) =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://example.test/") },
            Options.Create(o ?? new AnthropicOptions { Model = "m" }), NullLogger<AnthropicClient>.Instance);

    private static readonly JsonArray Tools = new(new JsonObject { ["type"] = "web_search_20260318", ["name"] = "web_search" });

    [Fact]
    public async Task StreamsTextDeltas_AndReturnsTheJoinedText()
    {
        var handler = new SseHandler(Sse(
            Start(), TextStart(0), TextDelta(0, "Hello "), ("ping", """{"type":"ping"}"""), TextDelta(0, "world"),
            BlockStop(0), Stop("end_turn"), End()));
        var progress = new Progress();

        var text = await Client(handler).StreamAsync("sys", "user", 0.3, 100, progress);

        Assert.Equal("Hello world", text);
        Assert.Equal(["Hello ", "world"], progress.Text);
        Assert.True(handler.Requests[0]["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StreamedUsage_IsRecordedWithTheTenantContext()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "anthropic.request"
                    && Equals(activity.GetTagItem("helpdesk.correlation_id"), "stream-correlation"))
                    stopped.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = Telemetry.Source.StartActivity("chat.test")
            ?? throw new InvalidOperationException("Activity listener was not active.");
        parent.SetTag("helpdesk.tenant_id", "stream-tenant");
        parent.SetTag("helpdesk.correlation_id", "stream-correlation");
        var handler = new SseHandler(Sse(
            ("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":4,"cache_read_input_tokens":5}}}"""),
            TextStart(0), TextDelta(0, "hi"), BlockStop(0),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}"""),
            End()));
        var options = new AnthropicOptions
        {
            Model = "m",
            InputCostPerMillionUsd = 10m,
            CacheWriteCostPerMillionUsd = 20m,
            CacheReadCostPerMillionUsd = 30m,
            OutputCostPerMillionUsd = 40m
        };

        await Client(handler, options).StreamAsync("sys", "user", 0.0, 100, new Progress());

        var activity = Assert.Single(stopped);
        Assert.Equal("stream-tenant", activity.GetTagItem("helpdesk.tenant_id"));
        Assert.Equal(11, activity.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(3, activity.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(0.00037m, activity.GetTagItem("gen_ai.usage.cost_usd"));
    }

    [Fact]
    public async Task Citations_AreAppended_AndStreamedSoDeltasMatchTheFinalText()
    {
        var handler = new SseHandler(Sse(
            Start(), TextStart(0), TextDelta(0, "Azure is up."),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"citations_delta","citation":{"url":"https://azure.status","title":"Azure status"}}}"""),
            BlockStop(0), Stop("end_turn"), End()));
        var progress = new Progress();

        var text = await Client(handler).StreamAsync("sys", "user", 0.3, 100, progress, Tools);

        Assert.Contains("- [Azure status](https://azure.status)", text);
        Assert.Equal(text, string.Concat(progress.Text));
    }

    [Fact]
    public async Task PauseTurn_RebuildsServerToolBlocks_AndResumes()
    {
        var paused = Sse(
            Start(),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"t1","name":"web_search","input":{}}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"azure status\"}"}}"""),
            BlockStop(0), Stop("pause_turn"), End());
        var done = Sse(Start(), TextStart(0), TextDelta(0, "done"), BlockStop(0), Stop("end_turn"), End());
        var handler = new SseHandler(paused, done);

        var text = await Client(handler).StreamAsync("sys", "user", 0.3, 100, new Progress(), Tools);

        Assert.Equal("done", text);
        Assert.Equal(2, handler.Requests.Count);
        var assistant = handler.Requests[1]["messages"]![1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        var block = assistant["content"]![0]!;
        Assert.Equal("server_tool_use", block["type"]!.GetValue<string>());
        Assert.Equal("azure status", block["input"]!["query"]!.GetValue<string>());
    }

    [Fact]
    public async Task StreamEndingBeforeMessageStop_IsAnError()
    {
        var handler = new SseHandler(Sse(Start(), TextStart(0), TextDelta(0, "partial")));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(handler).StreamAsync("sys", "user", 0.3, 100, new Progress()));
    }

    [Fact]
    public async Task ErrorEvent_Throws()
    {
        var handler = new SseHandler(Sse(
            Start(), ("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(handler).StreamAsync("sys", "user", 0.3, 100, new Progress()));

        Assert.Contains("overloaded_error", ex.Message);
    }

    [Fact]
    public async Task SilentStream_IsAbandonedAfterTheIdleTimeout()
    {
        var client = Client(new StallingHandler(), new AnthropicOptions { Model = "m", StreamIdleTimeoutSeconds = 1 });

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.StreamAsync("sys", "user", 0.3, 100, new Progress()));
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsAnIdleTimeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(new StallingHandler()).StreamAsync("sys", "user", 0.3, 100, new Progress(), cancellationToken: cts.Token));
    }
}
