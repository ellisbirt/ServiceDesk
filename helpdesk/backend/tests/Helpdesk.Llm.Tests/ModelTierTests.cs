using Helpdesk.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Llm.Tests;

/// <summary>Each vendor provides the tiers; a vendor with fewer models serves several tiers with the same one.</summary>
public class ModelTierTests
{
    private static ILlmProviderRouter Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddLlmProviders(configuration.GetSection(LlmOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<ILlmProviderRouter>();
    }

    private static Dictionary<string, string?> Chat(string provider, string model, params (string Key, string Value)[] more)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Llm:Roles:chat:Provider"] = provider,
            ["Llm:Roles:chat:Model"] = model,
            ["Llm:Roles:chat:ApiKey"] = "k"
        };
        foreach (var (key, value) in more) settings[$"Llm:Roles:chat:{key}"] = value;
        return settings;
    }

    [Fact]
    public void Anthropic_ProvidesHaikuSonnetAndOpus_ByDefault()
    {
        var router = Build(Chat("anthropic", "claude-sonnet-5-5"));

        Assert.Equal("claude-haiku-5-5", router.GetChat("chat", ModelTier.Fast).Model);
        Assert.Equal("claude-sonnet-5-5", router.GetChat("chat").Model);
        Assert.Equal("claude-sonnet-5-5", router.GetChat("chat", ModelTier.Standard).Model);
        Assert.Equal("claude-opus-5-5", router.GetChat("chat", ModelTier.Deep).Model);
    }

    [Fact]
    public void ConfiguredTierModels_ReplaceTheVendorDefaults()
    {
        var router = Build(Chat("anthropic", "claude-sonnet-5-5",
            ("FastTier:Model", "my-small"), ("DeepTier:Model", "my-big")));

        Assert.Equal("my-small", router.GetChat("chat", ModelTier.Fast).Model);
        Assert.Equal("my-big", router.GetChat("chat", ModelTier.Deep).Model);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure_openai")]
    [InlineData("grok")]
    [InlineData("ollama")]
    [InlineData("lm_studio")]
    public void VendorsWithoutKnownTierModels_ServeEveryTierWithTheRolesModel(string provider)
    {
        var router = Build(Chat(provider, "the-model", ("Endpoint", "https://example.invalid/v1"), ("ApiVersion", "2024-01-01")));

        Assert.Equal("the-model", router.GetChat("chat", ModelTier.Fast).Model);
        Assert.Equal("the-model", router.GetChat("chat", ModelTier.Standard).Model);
        Assert.Equal("the-model", router.GetChat("chat", ModelTier.Deep).Model);
    }

    [Fact]
    public void ALocalServerCanStillBeGivenSeparateTierModels()
    {
        var router = Build(Chat("lm_studio", "local-medium", ("FastTier:Model", "local-small")));

        Assert.Equal("local-small", router.GetChat("chat", ModelTier.Fast).Model);
        Assert.Equal("local-medium", router.GetChat("chat", ModelTier.Deep).Model);
    }

    [Fact]
    public void Providers_AreCachedPerRoleAndTier()
    {
        var router = Build(Chat("anthropic", "claude-sonnet-5-5"));

        Assert.Same(router.GetChat("chat", ModelTier.Fast), router.GetChat("CHAT", ModelTier.Fast));
        Assert.NotSame(router.GetChat("chat", ModelTier.Fast), router.GetChat("chat", ModelTier.Deep));
    }

    [Fact]
    public void ATierModel_IsNotPricedAtTheStandardModelsRates()
    {
        var options = new LlmProviderOptions
        {
            Provider = LlmProviders.Anthropic,
            Model = "claude-sonnet-5-5",
            InputCostPerMillionUsd = 3,
            OutputCostPerMillionUsd = 15,
            FastTier = new LlmTierOptions { Model = "claude-haiku-5-5", InputCostPerMillionUsd = 1, OutputCostPerMillionUsd = 5 }
        };

        var fast = options.ForTier(ModelTier.Fast);
        var deep = options.ForTier(ModelTier.Deep);

        Assert.Equal(1m, fast.InputCostPerMillionUsd);
        Assert.Equal(5m, fast.OutputCostPerMillionUsd);
        Assert.Null(deep.InputCostPerMillionUsd); // no rate was given for the Deep model, so none is estimated
        Assert.Null(deep.OutputCostPerMillionUsd);
        Assert.Equal(3m, options.ForTier(ModelTier.Standard).InputCostPerMillionUsd);
    }

    [Fact]
    public void ATierThatResolvesToTheRolesModel_KeepsTheRolesCosts()
    {
        var options = new LlmProviderOptions
        {
            Provider = LlmProviders.OpenAI, Model = "gpt-x", InputCostPerMillionUsd = 2
        };

        Assert.Same(options, options.ForTier(ModelTier.Fast));
        Assert.Equal(2m, options.ForTier(ModelTier.Deep).InputCostPerMillionUsd);
    }

    [Fact]
    public async Task TheTierDecorator_SendsEachRequestToItsTiersModel()
    {
        var calls = new List<(string Model, ModelTier Tier)>();
        var router = new RecordingRouter(calls);
        var provider = new TierRoutingChatProvider(router, "chat");

        await provider.CompleteAsync(new ChatRequest("s", "u") { Tier = ModelTier.Fast });
        await provider.CompleteAsync(new ChatRequest("s", "u"));
        await provider.CompleteAsync(new ChatRequest("s", "u") { Tier = ModelTier.Deep });

        Assert.Equal([("small", ModelTier.Fast), ("medium", ModelTier.Standard), ("large", ModelTier.Deep)], calls);
        Assert.Equal("medium", provider.Model); // it describes the Standard tier
    }

    [Fact]
    public async Task ARefusedFastRequest_IsRepeatedOnceOnTheStandardModel_ButAStreamIsNot()
    {
        var calls = new List<(string Model, ModelTier Tier)>();
        var router = new RecordingRouter(calls) { RefuseModels = { "small" } };
        var provider = new TierRoutingChatProvider(router, "chat");

        var text = await provider.CompleteAsync(new ChatRequest("s", "u") { Tier = ModelTier.Fast });

        Assert.Equal("reply from medium", text);
        Assert.Equal([("small", ModelTier.Fast), ("medium", ModelTier.Standard)], calls);

        calls.Clear();
        await Assert.ThrowsAsync<LlmRefusalException>(() =>
            provider.StreamAsync(new ChatRequest("s", "u") { Tier = ModelTier.Fast }, new NullProgress()));
        Assert.Equal([("small", ModelTier.Fast)], calls);
    }

    [Fact]
    public async Task WhenFastAndStandardAreTheSameModel_ARefusalIsNotRetried()
    {
        var calls = new List<(string Model, ModelTier Tier)>();
        var router = new RecordingRouter(calls) { FastModel = "medium", RefuseModels = { "medium" } };
        var provider = new TierRoutingChatProvider(router, "chat");

        await Assert.ThrowsAsync<LlmRefusalException>(() =>
            provider.CompleteAsync(new ChatRequest("s", "u") { Tier = ModelTier.Fast }));
        Assert.Single(calls);
    }

    private sealed class NullProgress : ILlmProgress
    {
        public ValueTask OnTextAsync(string delta, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask OnRateLimitedAsync(TimeSpan retryAfter, int attempt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingRouter(List<(string Model, ModelTier Tier)> calls) : ILlmProviderRouter
    {
        public string FastModel { get; init; } = "small";
        public HashSet<string> RefuseModels { get; } = [];

        public bool HasRole(string role) => true;
        public IEmbeddingProvider GetEmbedding(string role) => throw new NotSupportedException();
        public LlmProviderOptions? Describe(string role) => null;

        public IChatProvider GetChat(string role, ModelTier tier = ModelTier.Standard) =>
            new Fake(tier switch { ModelTier.Fast => FastModel, ModelTier.Deep => "large", _ => "medium" }, tier, calls, RefuseModels);
    }

    private sealed class Fake(string model, ModelTier tier, List<(string Model, ModelTier Tier)> calls, HashSet<string> refuse) : IChatProvider
    {
        public string Provider => "fake";
        public string Model => model;
        public LlmCapabilities Capabilities => LlmCapabilities.None;

        public Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default)
        {
            calls.Add((model, tier));
            return refuse.Contains(model) ? throw new LlmRefusalException("declined") : Task.FromResult($"reply from {model}");
        }

        public Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default)
        {
            calls.Add((model, tier));
            return refuse.Contains(model) ? throw new LlmRefusalException("declined") : Task.FromResult($"reply from {model}");
        }
    }
}
