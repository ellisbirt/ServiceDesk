using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Search.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Infrastructure.Search;

/// <summary>
/// The list of known providers. To add one: implement <see cref="ISearchProvider"/> with a static
/// <see cref="SearchProviderDefinition"/>, then add it here.
/// </summary>
public static class SearchProviderCatalog
{
    public static readonly IReadOnlyList<SearchProviderDefinition> All =
    [
        TavilySearchProvider.Definition,
        BraveSearchProvider.Definition,
        SerperSearchProvider.Definition,
        GoogleSearchProvider.Definition,
        SearxngSearchProvider.Definition,
    ];

    public static SearchProviderDefinition? Find(string name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "Claude" is not a provider in <see cref="All"/>: the model searches the web itself through Anthropic's
    /// server-side web search tool, so no results are fetched or formatted on our side.
    /// </summary>
    public const string NativeClaude = "Claude";

    public const string Tavily = "Tavily";

    public static bool IsNativeClaude(string name) =>
        string.Equals(name, NativeClaude, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Names => All.Select(d => d.Name).Append(NativeClaude);
}

/// <summary>Used with Claude-native search: the inference call does the searching, so there is nothing to pre-fetch.</summary>
public sealed class NativeClaudeSearchService : ILiveSearchService
{
    public Task<string> GetLiveSearchResultsAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);
}

public static class SearchServiceCollectionExtensions
{
    private const string HttpClientName = "search-provider";

    public static IServiceCollection AddLiveSearch(this IServiceCollection services, IConfiguration config)
    {
        var search = config.GetSection(SearchOptions.SectionName).Get<SearchOptions>() ?? new();
        // Search:Provider may be empty, meaning "the same vendor as the chat model where it offers search".
        search.Provider = ProviderAffinity.SearchProvider(config);
        services.Configure<SearchOptions>(config.GetSection(SearchOptions.SectionName));
        services.PostConfigure<SearchOptions>(o => o.Provider = search.Provider);

        if (SearchProviderCatalog.IsNativeClaude(search.Provider))
        {
            services.AddSingleton<ILiveSearchService, NativeClaudeSearchService>();
            return services;
        }

        var definition = SearchProviderCatalog.Find(search.Provider)
            ?? throw new InvalidOperationException(
                $"Unknown Search:Provider '{search.Provider}'. Available: {string.Join(", ", SearchProviderCatalog.Names)}.");

        var options = config.GetSection($"{SearchOptions.SectionName}:Providers:{definition.Name}")
            .Get<SearchProviderOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.ApiKey)) options.ApiKey = search.ApiKey;

        if (!definition.IsConfigured(options))
        {
            // Not configured: degrade to a stub rather than failing, so local development works without a key.
            services.AddSingleton<ILiveSearchService, StubLiveSearchService>();
            return services;
        }

        services.AddHttpClient(HttpClientName, http =>
        {
            http.BaseAddress = definition.BaseAddress(options);
            http.Timeout = Timeout.InfiniteTimeSpan; // owned by the resilience handler
            definition.ConfigureClient(http, options);
        }).AddHelpdeskResilience(attemptTimeout: TimeSpan.FromSeconds(10), totalTimeout: TimeSpan.FromSeconds(25));
        services.AddScoped<ISearchProvider>(sp =>
            definition.Create(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName), options));
        services.AddScoped<ILiveSearchService, LiveSearchService>();
        return services;
    }
}
