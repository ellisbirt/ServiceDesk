using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

/// <summary>Search and embeddings follow the chat vendor where it offers them; anything set explicitly wins.</summary>
public class ProviderAffinityTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    // ----- Search -----

    [Fact]
    public void Anthropic_UsesItsOwnWebSearch_ByDefault()
    {
        var config = Config(("Anthropic:ApiKey", "k"));

        Assert.Equal(SearchProviderCatalog.NativeClaude, ProviderAffinity.SearchProvider(config));
    }

    [Fact]
    public void ExplicitSearchProvider_Wins()
    {
        var config = Config(("Anthropic:ApiKey", "k"), ("Search:Provider", "Brave"));

        Assert.Equal("Brave", ProviderAffinity.SearchProvider(config));
    }

    [Fact]
    public void AThirdPartyProviderWithCredentials_IsKept_SoExistingSetupsDoNotChange()
    {
        var config = Config(("Anthropic:ApiKey", "k"), ("Search:Providers:Serper:ApiKey", "s"));

        Assert.Equal("Serper", ProviderAffinity.SearchProvider(config));
    }

    [Fact]
    public void TheLegacySearchApiKey_StillMeansTavily()
    {
        var config = Config(("Anthropic:ApiKey", "k"), ("Search:ApiKey", "s"));

        Assert.Equal(SearchProviderCatalog.Tavily, ProviderAffinity.SearchProvider(config));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure_openai")]
    [InlineData("ollama")]
    public void VendorsWithoutNativeSearch_FallBackToTavily(string vendor)
    {
        var config = Config(("Llm:Roles:chat:Provider", vendor), ("Llm:Roles:chat:Model", "m"));

        Assert.Equal(SearchProviderCatalog.Tavily, ProviderAffinity.SearchProvider(config));
    }

    [Fact]
    public void TheApp_ResolvesTheAutomaticSearchProvider()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Anthropic:ApiKey"] = "k" });

        Assert.Equal(SearchProviderCatalog.NativeClaude, f.Services.GetRequiredService<IOptions<SearchOptions>>().Value.Provider);
    }

    // ----- Embeddings -----

    [Fact]
    public void OpenAi_ChatGivesOpenAi_Embeddings_SharingItsEndpointAndKey()
    {
        var config = Config(
            ("Llm:Roles:chat:Provider", "openai"), ("Llm:Roles:chat:Model", "gpt-test"),
            ("Llm:Roles:chat:ApiKey", "k"), ("Llm:Roles:chat:Endpoint", "https://gateway.example/v1"));

        var role = ProviderAffinity.DerivedEmbeddingRole(config)!;

        Assert.Equal(LlmProviders.OpenAI, role.Provider);
        Assert.Equal(ProviderAffinity.OpenAiEmbeddingModel, role.Model);
        Assert.Equal(ProviderAffinity.OpenAiEmbeddingDimensions, role.Dimensions);
        Assert.Equal("k", role.ApiKey);
        Assert.Equal("https://gateway.example/v1", role.Endpoint);
    }

    [Fact]
    public void AnExplicitEmbeddingRole_IsNeverReplaced()
    {
        var config = Config(
            ("Llm:Roles:chat:Provider", "openai"), ("Llm:Roles:chat:Model", "gpt-test"),
            ("Llm:Roles:embedding:Provider", "ollama"), ("Llm:Roles:embedding:Model", "nomic-embed-text"));

        Assert.Null(ProviderAffinity.DerivedEmbeddingRole(config));
    }

    [Fact]
    public void TheConfiguredEmbeddingRoleName_IsHonoured()
    {
        var config = Config(
            ("Kb:EmbeddingRole", "kb-embed"),
            ("Llm:Roles:chat:Provider", "openai"), ("Llm:Roles:chat:Model", "gpt-test"),
            ("Llm:Roles:embedding:Provider", "ollama"));

        Assert.NotNull(ProviderAffinity.DerivedEmbeddingRole(config));
    }

    [Theory]
    [InlineData("anthropic")]    // no embeddings API
    [InlineData("grok")]         // no embeddings API
    [InlineData("azure_openai")] // needs the operator's deployment name
    [InlineData("ollama")]       // needs the operator's model name
    [InlineData("lm_studio")]
    public void VendorsWithoutAKnownDefaultModel_DeriveNothing(string vendor)
    {
        var config = Config(("Llm:Roles:chat:Provider", vendor), ("Llm:Roles:chat:Model", "m"));

        Assert.Null(ProviderAffinity.DerivedEmbeddingRole(config));
    }

    [Fact]
    public void TheApp_RegistersTheDerivedEmbeddingRole()
    {
        // Development config defines an offline embedding role, so redirect the KB at a role it does not define.
        using var f = new ApiFactory(settings: new Dictionary<string, string>
        {
            ["Kb:EmbeddingRole"] = "kb-embed",
            ["Llm:Roles:chat:Provider"] = "openai",
            ["Llm:Roles:chat:Model"] = "gpt-test",
            ["Llm:Roles:chat:ApiKey"] = "k"
        });
        var router = f.Services.GetRequiredService<ILlmProviderRouter>();

        Assert.True(router.HasRole("kb-embed"));
        var described = router.Describe("kb-embed")!;
        Assert.Equal(LlmProviders.OpenAI, described.Provider);
        Assert.Equal(ProviderAffinity.OpenAiEmbeddingModel, described.Model);
    }
}
