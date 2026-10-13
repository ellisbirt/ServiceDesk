using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Helpdesk.Llm;
using Helpdesk.Llm.Anthropic;
using Helpdesk.Llm.Offline;
using Helpdesk.Llm.OpenAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Llm.Tests;

internal sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(string events) =>
        new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
}

internal sealed class RecordingProgress : ILlmProgress
{
    public StringBuilder Text { get; } = new();
    public List<TimeSpan> Waits { get; } = [];

    public ValueTask OnTextAsync(string text, CancellationToken cancellationToken)
    {
        Text.Append(text);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnRateLimitedAsync(TimeSpan wait, int attempt, CancellationToken cancellationToken)
    {
        Waits.Add(wait);
        return ValueTask.CompletedTask;
    }
}

public class OpenAiCompatibleChatProviderTests
{
    private static OpenAiCompatibleChatProvider Create(string provider, StubHandler handler, Action<LlmProviderOptions>? configure = null)
    {
        var options = new LlmProviderOptions { Provider = provider, Model = "gpt-4o-mini", ApiKey = "secret", MaxTokens = 200 };
        configure?.Invoke(options);
        return new OpenAiCompatibleChatProvider(provider, new HttpClient(handler), options, NullLogger<OpenAiCompatibleChatProvider>.Instance);
    }

    private const string Completion = """
        {"choices":[{"message":{"role":"assistant","content":"Restart the router."}}],
         "usage":{"prompt_tokens":120,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":20}}}
        """;

    [Fact]
    public async Task OpenAi_PostsChatCompletionWithBearerAndSchema()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(Completion));
        var provider = Create(LlmProviders.OpenAI, handler);
        var schema = JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""")!.AsObject();

        var text = await provider.CompleteAsync(new ChatRequest("sys", "user text", 0.2, ResponseSchema: schema));

        Assert.Equal("Restart the router.", text);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("system", json["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("user text", json["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal(200, json["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(json["max_tokens"]);
        Assert.Equal(0.2, json["temperature"]!.GetValue<double>());
        Assert.Equal("json_schema", json["response_format"]!["type"]!.GetValue<string>());
        Assert.True(json["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AzureOpenAi_UsesDeploymentPathAndApiKeyHeader()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(Completion));
        var provider = Create(LlmProviders.AzureOpenAI, handler, o =>
        {
            o.Endpoint = "https://contoso.openai.azure.com/";
            o.Model = "chat-deploy";
        });

        await provider.CompleteAsync(new ChatRequest("sys", "hi", 0));

        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal("https://contoso.openai.azure.com/openai/deployments/chat-deploy/chat/completions?api-version=2024-10-21",
            request.RequestUri!.ToString());
        Assert.Equal("secret", request.Headers.GetValues("api-key").Single());
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Ollama_UsesLocalEndpointMaxTokensAndNoAuth()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(Completion));
        var provider = Create(LlmProviders.Ollama, handler, o => { o.ApiKey = null; o.Model = "llama3.1"; });

        await provider.CompleteAsync(new ChatRequest("sys", "hi", 0));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.RequestUri!.ToString());
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Equal(200, JsonNode.Parse(body)!["max_tokens"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("o3-mini", false)]
    [InlineData("gpt-5-mini", false)]
    [InlineData("gpt-4o", true)]
    [InlineData("llama3.1", true)]
    public void TemperatureHeuristic(string model, bool expected) =>
        Assert.Equal(expected, OpenAiCompatibleChatProvider.SupportsTemperature(model));

    [Fact]
    public async Task NonStrictSchema_IsSentWithStrictFalse()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(Completion));
        var provider = Create(LlmProviders.OpenAI, handler);
        var schema = JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"}}}""")!.AsObject();

        await provider.CompleteAsync(new ChatRequest("sys", "hi", 0, ResponseSchema: schema));

        Assert.False(JsonNode.Parse(handler.Requests[0].Body)!["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ServerTools_AreRejected()
    {
        var provider = Create(LlmProviders.OpenAI, new StubHandler((_, _) => StubHandler.Json(Completion)));
        await Assert.ThrowsAsync<LlmCapabilityException>(() =>
            provider.CompleteAsync(new ChatRequest("sys", "hi", 0, Tools: [new JsonObject { ["type"] = "web_search" }])));
    }

    [Fact]
    public async Task Streaming_ForwardsDeltasUntilDone()
    {
        const string events = """
            data: {"choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"content":"Hello "}}]}

            data: {"choices":[{"delta":{"content":"there"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":2}}

            data: [DONE]


            """;
        var handler = new StubHandler((_, _) => StubHandler.Sse(events));
        var provider = Create(LlmProviders.OpenAI, handler);
        var progress = new RecordingProgress();

        var text = await provider.StreamAsync(new ChatRequest("sys", "hi", 0), progress);

        Assert.Equal("Hello there", text);
        Assert.Equal(text, progress.Text.ToString());
        var body = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Streaming_ThatEndsEarly_Throws()
    {
        var handler = new StubHandler((_, _) => StubHandler.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n"));
        var provider = Create(LlmProviders.OpenAI, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.StreamAsync(new ChatRequest("sys", "hi", 0), new RecordingProgress()));
    }

    [Fact]
    public async Task EmptyContent_Throws()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""{"choices":[{"message":{"content":""}}]}"""));
        var provider = Create(LlmProviders.OpenAI, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(new ChatRequest("sys", "hi", 0)));
    }

    [Fact]
    public async Task RateLimit_IsRetriedAfterRetryAfter()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            if (calls++ == 0)
            {
                var limited = StubHandler.Json("{}", HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }
            return StubHandler.Json(Completion);
        });
        var provider = Create(LlmProviders.OpenAI, handler);
        var progress = new RecordingProgress();

        var text = await provider.CompleteAsync(new ChatRequest("sys", "hi", 0), progress);

        Assert.Equal("Restart the router.", text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(progress.Waits);
    }
}

public class EmbeddingProviderTests
{
    [Fact]
    public async Task OpenAiEmbeddings_AreOrderedByIndexAndValidated()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""
            {"data":[{"index":1,"embedding":[0,1,0]},{"index":0,"embedding":[1,0,0]}],"usage":{"prompt_tokens":4}}
            """));
        var provider = new OpenAiCompatibleEmbeddingProvider(LlmProviders.OpenAI, new HttpClient(handler),
            new LlmProviderOptions { Model = "custom-embed", ApiKey = "k", Dimensions = 3 },
            NullLogger<OpenAiCompatibleEmbeddingProvider>.Instance);

        var vectors = await provider.EmbedAsync(["first", "second"]);

        Assert.Equal([1f, 0f, 0f], vectors[0]);
        Assert.Equal([0f, 1f, 0f], vectors[1]);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/embeddings", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("custom-embed", json["model"]!.GetValue<string>());
        Assert.Null(json["dimensions"]); // only text-embedding-3 supports shortening
    }

    [Fact]
    public async Task WrongDimensions_Throws()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""{"data":[{"index":0,"embedding":[1,0]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(LlmProviders.Ollama, new HttpClient(handler),
            new LlmProviderOptions { Model = "nomic-embed-text" }, NullLogger<OpenAiCompatibleEmbeddingProvider>.Instance);

        Assert.Equal(768, provider.Dimensions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedAsync(["x"]));
    }

    [Fact]
    public async Task AzureEmbeddings_UseDeploymentPath()
    {
        var vector = string.Join(',', Enumerable.Repeat("0", 1536));
        var handler = new StubHandler((_, _) => StubHandler.Json($$"""{"data":[{"index":0,"embedding":[{{vector}}]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(LlmProviders.AzureOpenAI, new HttpClient(handler),
            new LlmProviderOptions { Model = "text-embedding-3-small", Endpoint = "https://x.openai.azure.com", ApiKey = "k", ApiVersion = "2024-06-01" },
            NullLogger<OpenAiCompatibleEmbeddingProvider>.Instance);

        await provider.EmbedAsync(["x"]);

        Assert.Equal("https://x.openai.azure.com/openai/deployments/text-embedding-3-small/embeddings?api-version=2024-06-01",
            handler.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public void UnknownModelWithoutDimensions_Throws() =>
        Assert.Throws<InvalidOperationException>(() => new OpenAiCompatibleEmbeddingProvider(LlmProviders.Ollama,
            new HttpClient(), new LlmProviderOptions { Model = "mystery" }, NullLogger<OpenAiCompatibleEmbeddingProvider>.Instance));

    [Fact]
    public async Task OfflineEmbeddings_AreDeterministicNormalisedAndSimilarForSharedWords()
    {
        var provider = new OfflineEmbeddingProvider(new LlmProviderOptions { Dimensions = 256 });

        var vectors = await provider.EmbedAsync(["printer offline in office", "printer offline", "vpn certificate expired"]);
        var again = await provider.EmbedAsync(["printer offline in office"]);

        Assert.Equal(256, vectors[0].Length);
        Assert.Equal(vectors[0], again[0]);
        Assert.Equal(1.0, Math.Sqrt(vectors[0].Sum(v => (double)v * v)), 4);
        Assert.True(Cosine(vectors[0], vectors[1]) > Cosine(vectors[0], vectors[2]));
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
}

public class OfflineChatProviderTests
{
    [Fact]
    public async Task Schema_ProducesConformingSample()
    {
        var provider = new OfflineChatProvider(new LlmProviderOptions());
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{
              "category":{"type":"string","enum":["hardware","software"]},
              "confidence":{"type":"number"},
              "steps":{"type":"array","items":{"type":"string"}},
              "urgent":{"type":"boolean"},
              "nested":{"type":"object","properties":{"title":{"type":"string"}}}}}
            """)!.AsObject();

        var json = JsonNode.Parse(await provider.CompleteAsync(new ChatRequest("s", "u", 0, ResponseSchema: schema)))!;

        Assert.Equal("hardware", json["category"]!.GetValue<string>());
        Assert.Equal(0, json["confidence"]!.GetValue<int>());
        Assert.Empty(json["steps"]!.AsArray());
        Assert.False(json["urgent"]!.GetValue<bool>());
        Assert.Equal("", json["nested"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Streaming_ConcatenatesToResult()
    {
        var provider = new OfflineChatProvider(new LlmProviderOptions());
        var progress = new RecordingProgress();

        var text = await provider.StreamAsync(new ChatRequest("s", "u", 0), progress);

        Assert.Equal(text, progress.Text.ToString());
        Assert.Contains("offline", text);
    }
}

public class AnthropicChatProviderTests
{
    [Fact]
    public async Task SendsKeyAndVersionPerRequestAndCachesSystemPrompt()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""
            {"content":[{"type":"text","text":"Done."}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":1}}
            """));
        var provider = new AnthropicChatProvider(new HttpClient(handler),
            new LlmProviderOptions { Provider = "anthropic", Model = "claude-sonnet-4-5", ApiKey = "ak" },
            NullLogger<AnthropicChatProvider>.Instance);

        var text = await provider.CompleteAsync(new ChatRequest("sys", "hi", 0.3));

        Assert.Equal("Done.", text);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal("ak", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("ephemeral", json["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal(0.3, json["temperature"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("claude-3-5-haiku-20241022", true)]
    [InlineData("claude-latest", false)]
    public void TemperatureHeuristic(string model, bool expected) =>
        Assert.Equal(expected, AnthropicChatProvider.SupportsTemperature(model));
}

public class LlmProviderRouterTests
{
    private static ILlmProviderRouter Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddLlmProviders(configuration.GetSection(LlmOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<ILlmProviderRouter>();
    }

    [Fact]
    public void ResolvesRolesCaseInsensitivelyWithAliases()
    {
        var router = Build(new()
        {
            ["Llm:Roles:Assist:Provider"] = "fake",
            ["Llm:Roles:embedding:Provider"] = "lmstudio",
            ["Llm:Roles:embedding:Model"] = "nomic-embed-text-v1.5",
        });

        Assert.IsType<OfflineChatProvider>(router.GetChat("assist"));
        Assert.Same(router.GetChat("assist"), router.GetChat("ASSIST"));
        var embedding = router.GetEmbedding("embedding");
        Assert.Equal(LlmProviders.LMStudio, embedding.Provider);
        Assert.Equal(768, embedding.Dimensions);
        Assert.True(router.HasRole("Embedding"));
        Assert.False(router.HasRole("missing"));
    }

    [Fact]
    public void UnknownRoleAndProvider_Throw()
    {
        var router = Build(new() { ["Llm:Roles:chat:Provider"] = "bogus" });

        Assert.Throws<InvalidOperationException>(() => router.GetChat("nope"));
        Assert.Throws<InvalidOperationException>(() => router.GetChat("chat"));
    }

    [Fact]
    public void AnthropicEmbeddings_AreRejected()
    {
        var router = Build(new()
        {
            ["Llm:Roles:embedding:Provider"] = "anthropic",
            ["Llm:Roles:embedding:Model"] = "claude",
            ["Llm:Roles:embedding:ApiKey"] = "x",
        });

        Assert.Throws<LlmCapabilityException>(() => router.GetEmbedding("embedding"));
    }

    [Fact]
    public void Describe_RedactsKey()
    {
        var router = Build(new()
        {
            ["Llm:Roles:chat:Provider"] = "openai",
            ["Llm:Roles:chat:Model"] = "gpt-4o",
            ["Llm:Roles:chat:ApiKey"] = "sk-secret",
        });

        Assert.Equal("***", router.Describe("chat")!.ApiKey);
        Assert.Null(router.Describe("other"));
    }
}
