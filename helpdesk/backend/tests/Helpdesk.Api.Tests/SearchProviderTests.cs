using System.Net;
using System.Text.Json.Nodes;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Infrastructure.Search.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

public class SearchProviderTests
{
    private sealed class StubHandler(params string[] jsonResponses) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            var json = jsonResponses[Math.Min(_next++, jsonResponses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private static HttpClient Client(StubHandler h) => new(h) { BaseAddress = new Uri("https://x.test/") };

    [Fact]
    public async Task Tavily_PostsQuery_AndMapsResults()
    {
        var h = new StubHandler("""{"results":[{"title":"A","url":"https://a.test","content":"alpha"}]}""");
        var r = await new TavilySearchProvider(Client(h)).SearchAsync("is azure down", 2, default);

        Assert.Equal(new SearchResult("A", "https://a.test", "alpha"), Assert.Single(r));
        Assert.Equal(HttpMethod.Post, h.Calls[0].Request.Method);
        Assert.Contains("\"query\":\"is azure down\"", h.Calls[0].Body);
        Assert.Contains("\"max_results\":2", h.Calls[0].Body);
    }

    [Fact]
    public async Task Brave_MapsResults_AndEscapesQuery()
    {
        var h = new StubHandler("""{"web":{"results":[{"title":"A","url":"https://a.test","description":"alpha"}]}}""");
        var r = await new BraveSearchProvider(Client(h)).SearchAsync("a b", 3, default);

        Assert.Equal("alpha", Assert.Single(r).Snippet);
        Assert.Contains("q=a%20b", h.Calls[0].Request.RequestUri!.Query);
        Assert.Contains("count=3", h.Calls[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Serper_MapsOrganicResults()
    {
        var h = new StubHandler("""{"organic":[{"title":"A","link":"https://a.test","snippet":"alpha"}]}""");
        var r = await new SerperSearchProvider(Client(h)).SearchAsync("q", 3, default);

        Assert.Equal("https://a.test", Assert.Single(r).Url);
        Assert.Contains("\"q\":\"q\"", h.Calls[0].Body);
    }

    [Fact]
    public async Task Google_SendsKeyAndEngineId_AndClampsNum()
    {
        var h = new StubHandler("""{"items":[{"title":"A","link":"https://a.test","snippet":"alpha"}]}""");
        var p = new GoogleSearchProvider(Client(h), new SearchProviderOptions { ApiKey = "K", SearchEngineId = "CX" });
        var r = await p.SearchAsync("q", 50, default);

        Assert.Single(r);
        var query = h.Calls[0].Request.RequestUri!.Query;
        Assert.Contains("key=K", query);
        Assert.Contains("cx=CX", query);
        Assert.Contains("num=10", query);
    }

    [Fact]
    public async Task Searxng_TrimsToMaxResults_AndRequestsJson()
    {
        var h = new StubHandler("""
            {"results":[{"title":"A","url":"https://a.test","content":"1"},{"title":"B","url":"https://b.test","content":"2"},{"title":"C","url":"https://c.test","content":"3"}]}
            """);
        var r = await new SearxngSearchProvider(Client(h)).SearchAsync("q", 2, default);

        Assert.Equal(2, r.Count);
        Assert.Contains("format=json", h.Calls[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Providers_SkipEntriesWithoutUrl()
    {
        var h = new StubHandler("""{"results":[{"title":"no url","content":"x"}]}""");
        Assert.Empty(await new TavilySearchProvider(Client(h)).SearchAsync("q", 3, default));
    }

    private sealed class FakeProvider(params SearchResult[] results) : ISearchProvider
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    [Fact]
    public async Task LiveSearchService_FormatsMarkdown_AndLimitsToTopResults()
    {
        var svc = new LiveSearchService(
            new FakeProvider(new("A", "https://a.test", "alpha"), new("B", "https://b.test", "beta")),
            Options.Create(new SearchOptions { TopResults = 1 }));

        var md = await svc.GetLiveSearchResultsAsync("q");

        Assert.Contains("### [A](https://a.test)", md);
        Assert.DoesNotContain("beta", md);
    }

    [Fact]
    public async Task LiveSearchService_NoResults_ReturnsPlaceholder() =>
        Assert.Contains("No search results",
            await new LiveSearchService(new FakeProvider(), Options.Create(new SearchOptions())).GetLiveSearchResultsAsync("q"));

    private static Type Resolve(Dictionary<string, string?> settings)
    {
        settings.TryAdd("Anthropic:ApiKey", "k");
        settings.TryAdd("StorageQueue:ConnectionString", "UseDevelopmentStorage=true");
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHelpdeskInfrastructure(config, new QueuePublisherTests.Env("Development"));
        using var scope = services.BuildServiceProvider().CreateScope();
        return scope.ServiceProvider.GetRequiredService<ILiveSearchService>().GetType();
    }

    // A chat vendor with no web search of its own, so the third-party default (Tavily) applies.
    private static Dictionary<string, string?> OpenAiChat() => new()
    {
        ["Llm:Roles:chat:Provider"] = "openai", ["Llm:Roles:chat:Model"] = "gpt-test", ["Llm:Roles:chat:ApiKey"] = "k"
    };

    [Fact]
    public void NoCredentials_UsesStub() => Assert.Equal(typeof(StubLiveSearchService), Resolve(OpenAiChat()));

    [Fact]
    public void AnthropicChat_WithNoSearchSettings_UsesItsOwnWebSearch() =>
        Assert.Equal(typeof(NativeClaudeSearchService), Resolve([]));

    [Fact]
    public void LegacyApiKey_AppliesToDefaultProvider() =>
        Assert.Equal(typeof(LiveSearchService), Resolve(new() { ["Search:ApiKey"] = "k" }));

    [Theory]
    [InlineData("Tavily", "Search:Providers:Tavily:ApiKey")]
    [InlineData("brave", "Search:Providers:Brave:ApiKey")]
    [InlineData("Serper", "Search:Providers:Serper:ApiKey")]
    [InlineData("SearXNG", "Search:Providers:SearXNG:BaseUrl")]
    public void EachProvider_CanBeSelectedByName(string provider, string settingKey) =>
        Assert.Equal(typeof(LiveSearchService),
            Resolve(new() { ["Search:Provider"] = provider, [settingKey] = "https://k.test" }));

    [Fact]
    public void Google_RequiresBothKeyAndEngineId()
    {
        Assert.Equal(typeof(StubLiveSearchService),
            Resolve(new() { ["Search:Provider"] = "Google", ["Search:Providers:Google:ApiKey"] = "k" }));
        Assert.Equal(typeof(LiveSearchService),
            Resolve(new()
            {
                ["Search:Provider"] = "Google",
                ["Search:Providers:Google:ApiKey"] = "k",
                ["Search:Providers:Google:SearchEngineId"] = "cx"
            }));
    }

    [Fact]
    public void Claude_UsesNativeSearch_WithoutAnyKey() =>
        Assert.Equal(typeof(NativeClaudeSearchService), Resolve(new() { ["Search:Provider"] = "Claude" }));

    [Fact]
    public void UnknownProvider_ThrowsListingAvailableNames()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(new() { ["Search:Provider"] = "Bing" }));
        Assert.Contains("Claude", ex.Message);
        Assert.Contains("SearXNG", ex.Message);
    }

    [Fact]
    public void Catalog_NamesAreUnique() =>
        Assert.Equal(SearchProviderCatalog.Names.Count(), SearchProviderCatalog.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
}

public class ClaudeNativeSearchTests
{
    private sealed class QueueHandler(params string[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<JsonNode> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Math.Min(_next++, responses.Length - 1)], System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private static TestAnthropic Client(QueueHandler h, string model = "m") =>
        new(h, new AnthropicOptions { Model = model });

    private static readonly JsonArray Tools = new(new JsonObject { ["type"] = "web_search_20260318", ["name"] = "web_search" });

    [Theory]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-sonnet-5-5", false)]
    [InlineData("claude-sonnet-latest", false)]
    public async Task Temperature_IsOnlySentForModelsThatSupportIt(string model, bool expected)
    {
        var h = new QueueHandler("""{"stop_reason":"end_turn","content":[{"type":"text","text":"hi"}]}""");

        await Client(h, model).CompleteAsync("sys", "user", 0.3, 100);

        Assert.Equal(expected, h.Requests[0]!.AsObject().ContainsKey("temperature"));
    }

    [Fact]
    public async Task Tools_AreSent_AndCitationsAppendedAsSources()
    {
        var h = new QueueHandler("""
            {"stop_reason":"end_turn","content":[
              {"type":"server_tool_use","name":"web_search"},
              {"type":"web_search_tool_result","content":[]},
              {"type":"text","text":"Azure is "},
              {"type":"text","text":"healthy.","citations":[{"url":"https://azure.status","title":"Azure status"},{"url":"https://azure.status","title":"dup"}]}]}
            """);

        var text = await Client(h).CompleteAsync("sys", "user", 0.3, 100, Tools);

        Assert.StartsWith("Azure is healthy.", text);
        Assert.Contains("- [Azure status](https://azure.status)", text);
        Assert.DoesNotContain("dup", text);
        Assert.Equal("web_search", h.Requests[0]["tools"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task PauseTurn_IsResumed_WithAssistantContentAppended()
    {
        var h = new QueueHandler(
            """{"stop_reason":"pause_turn","content":[{"type":"server_tool_use","name":"web_search"}]}""",
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}""");

        var text = await Client(h).CompleteAsync("sys", "user", 0.3, 100, Tools);

        Assert.Equal("done", text);
        Assert.Equal(2, h.Requests.Count);
        var messages = h.Requests[1]["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoToolsSent_WhenNoneRequested()
    {
        var h = new QueueHandler("""{"stop_reason":"end_turn","content":[{"type":"text","text":"hi"}]}""");
        await Client(h).CompleteAsync("sys", "user", 0.0, 100);
        Assert.Null(h.Requests[0]["tools"]);
    }

    [Fact]
    public async Task PauseTurn_IsBounded()
    {
        var h = new QueueHandler("""{"stop_reason":"pause_turn","content":[{"type":"text","text":"x"}]}""");
        await Client(h).CompleteAsync("sys", "user", 0.3, 100, Tools);
        Assert.Equal(4, h.Requests.Count);
    }

    private sealed class RecordingClient : IChatProvider
    {
        public string? System { get; private set; }
        public string? User { get; private set; }
        public JsonArray? ToolsSent { get; private set; }
        public JsonObject? ResponseSchemaSent { get; private set; }
        public bool Streamed { get; private set; }

        public string Provider => LlmProviders.Anthropic;
        public string Model => "m";
        public LlmCapabilities Capabilities =>
            LlmCapabilities.Streaming | LlmCapabilities.StructuredOutput | LlmCapabilities.ServerTools;

        public Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default)
        {
            (System, User, ToolsSent, ResponseSchemaSent) = (request.SystemPrompt, request.UserContent, request.Tools, request.ResponseSchema);
            return Task.FromResult("ok");
        }

        public Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default)
        {
            Streamed = true;
            (System, User, ToolsSent) = (request.SystemPrompt, request.UserContent, request.Tools);
            return Task.FromResult("ok");
        }
    }

    [Fact]
    public async Task Inference_WithClaudeProvider_UsesToolAndNativePrompt()
    {
        var client = new RecordingClient();
        var svc = new PublicInferenceService(
            client, Options.Create(new AnthropicOptions { WebSearchMaxUses = 2 }),
            Options.Create(new SearchOptions { Provider = "claude" }));

        await svc.ExecuteSearchInferenceAsync("is azure down", "");

        Assert.Equal(Prompts.PublicSearchNative, client.System);
        Assert.Equal("web_search_20260318", client.ToolsSent![0]!["type"]!.GetValue<string>());
        Assert.Equal(2, client.ToolsSent[0]!["max_uses"]!.GetValue<int>());
    }

    [Fact]
    public async Task Inference_WithOtherProvider_UsesProvidedContext_AndNoTools()
    {
        var client = new RecordingClient();
        var svc = new PublicInferenceService(
            client, Options.Create(new AnthropicOptions()), Options.Create(new SearchOptions { Provider = "Tavily" }));

        await svc.ExecuteSearchInferenceAsync("q", "CONTEXT");

        Assert.Equal(Prompts.PublicSearch, client.System);
        Assert.Contains("CONTEXT", client.User);
        Assert.Null(client.ToolsSent);
    }

    [Fact]
    public async Task Inference_PassesUserAgentInsideClientContextBoundaries()
    {
        var client = new RecordingClient();
        var svc = new PublicInferenceService(
            client, Options.Create(new AnthropicOptions()), Options.Create(new SearchOptions { Provider = "Tavily" }));

        await svc.ExecutePublicInferenceAsync("How can I check this?", userAgent: "Example/1 <ignore>");

        Assert.Contains("[BEGIN UNTRUSTED client_context ", client.User);
        Assert.Contains("Example/1 <ignore>", client.User);
    }
}
