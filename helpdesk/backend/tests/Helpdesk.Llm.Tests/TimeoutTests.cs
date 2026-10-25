using System.Net;
using System.Text;
using Helpdesk.Llm;
using Helpdesk.Llm.OpenAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Llm.Tests;

/// <summary>
/// A slow local model (Ollama, LM Studio on modest hardware) can take minutes before its first token, so every timeout on a
/// call to a model can be raised or switched off (0). These tests use short real delays to check both directions.
/// </summary>
public class TimeoutTests
{
    private sealed class DelayHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") };
        }
    }

    /// <summary>A stream whose first read waits, like a model that is still processing the prompt.</summary>
    private sealed class SlowStartStream(TimeSpan delay, string text) : Stream
    {
        private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes(text));
        private bool _started;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                _started = true;
                await Task.Delay(delay, cancellationToken);
            }
            return await _inner.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class SlowStartHandler(TimeSpan delay, string events) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new SlowStartStream(delay, events));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static HttpClient ModelClient(int attemptSeconds, int totalSeconds, TimeSpan serverDelay)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Llm:Http:AttemptTimeoutSeconds"] = attemptSeconds.ToString(),
            ["Llm:Http:TotalTimeoutSeconds"] = totalSeconds.ToString()
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLlmProviders(config.GetSection("Llm"));
        services.AddHttpClient(LlmProviderRouter.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new DelayHandler(serverDelay));
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(LlmProviderRouter.HttpClientName);
    }

    [Fact]
    public void TheDefaults_AreTheOnesForHostedVendors()
    {
        var http = new LlmHttpOptions();

        Assert.Equal(60, http.AttemptTimeoutSeconds);
        Assert.Equal(150, http.TotalTimeoutSeconds);
        Assert.Equal(30, new LlmProviderOptions().StreamIdleTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(60), http.AttemptTimeout);
        Assert.Equal(TimeSpan.FromSeconds(150), http.TotalTimeout);
        var unlimited = new LlmHttpOptions { AttemptTimeoutSeconds = 0, TotalTimeoutSeconds = 0 };
        Assert.Equal(TimeSpan.FromHours(12), unlimited.AttemptTimeout);
        Assert.Equal(TimeSpan.FromHours(24), unlimited.TotalTimeout);
    }

    [Theory]
    [InlineData(0, 150, 149)]          // no per-try limit, but the whole call is still limited: a try cannot outlast it
    [InlineData(120, 60, 59)]          // a per-try limit longer than the total is held below it
    [InlineData(90000, 0, 43200)]      // above the 12 hours the pipeline accepts
    [InlineData(-1, -1, 43200)]
    [InlineData(5, 1, 1)]
    public void TheEffectiveLimits_AreAlwaysOnesThePipelineAccepts(int attempt, int total, int expectedAttemptSeconds)
    {
        var http = new LlmHttpOptions { AttemptTimeoutSeconds = attempt, TotalTimeoutSeconds = total };

        Assert.True(http.AttemptTimeout >= TimeSpan.FromSeconds(1) && http.AttemptTimeout <= TimeSpan.FromHours(12));
        Assert.True(http.TotalTimeout > http.AttemptTimeout && http.TotalTimeout <= TimeSpan.FromHours(24));
        Assert.Equal(TimeSpan.FromSeconds(expectedAttemptSeconds), http.AttemptTimeout);
    }

    [Fact]
    public async Task ALimit_CutsOffASlowCall()
    {
        using var client = ModelClient(attemptSeconds: 1, totalSeconds: 2, serverDelay: TimeSpan.FromSeconds(4));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://model.test/v1/chat/completions"));
    }

    [Fact]
    public async Task ZeroMeansNoLimit_SoASlowCallCompletes()
    {
        using var client = ModelClient(attemptSeconds: 0, totalSeconds: 0, serverDelay: TimeSpan.FromSeconds(3));

        using var response = await client.GetAsync("http://model.test/v1/chat/completions");

        Assert.Equal("done", await response.Content.ReadAsStringAsync());
    }

    private const string Events = """
        data: {"choices":[{"delta":{"content":"Hello"},"finish_reason":"stop"}]}

        data: [DONE]


        """;

    private static OpenAiCompatibleChatProvider LocalProvider(int idleSeconds) => new(
        LlmProviders.LMStudio, new HttpClient(new SlowStartHandler(TimeSpan.FromSeconds(3), Events)),
        new LlmProviderOptions { Provider = LlmProviders.LMStudio, Model = "local", MaxTokens = 50, StreamIdleTimeoutSeconds = idleSeconds },
        NullLogger<OpenAiCompatibleChatProvider>.Instance);

    private sealed class Ignore : ILlmProgress
    {
        public ValueTask OnTextAsync(string delta, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnRateLimitedAsync(TimeSpan retryAfter, int attempt, CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AStreamThatIsSilentForTooLong_IsAbandoned()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => LocalProvider(idleSeconds: 1).StreamAsync(new ChatRequest("sys", "hi", 0), new Ignore()));
    }

    [Fact]
    public async Task ZeroIdleTimeout_WaitsForASlowStart()
    {
        var text = await LocalProvider(idleSeconds: 0).StreamAsync(new ChatRequest("sys", "hi", 0), new Ignore());

        Assert.Equal("Hello", text);
    }
}
