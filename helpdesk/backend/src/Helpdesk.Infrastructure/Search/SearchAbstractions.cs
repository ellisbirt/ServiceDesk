using System.Text;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Search;

public sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>One web-search backend. Implement this, describe it with a <see cref="SearchProviderDefinition"/>, and add it to <see cref="SearchProviderCatalog"/>.</summary>
public interface ISearchProvider
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken);
}

/// <summary>Per-provider settings, bound from <c>Search:Providers:{Name}</c>.</summary>
public sealed class SearchProviderOptions
{
    public string ApiKey { get; set; } = "";
    /// <summary>Overrides the provider's default endpoint. Required for self-hosted providers such as SearXNG.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Google Programmable Search engine id (cx).</summary>
    public string SearchEngineId { get; set; } = "";
}

/// <summary>Self-description of a provider so the registry can validate, configure and create it.</summary>
public sealed record SearchProviderDefinition(
    string Name,
    string DefaultBaseUrl,
    Func<SearchProviderOptions, bool> IsConfigured,
    Action<HttpClient, SearchProviderOptions> ConfigureClient,
    Func<HttpClient, SearchProviderOptions, ISearchProvider> Create)
{
    public Uri BaseAddress(SearchProviderOptions o) =>
        new((string.IsNullOrWhiteSpace(o.BaseUrl) ? DefaultBaseUrl : o.BaseUrl).TrimEnd('/') + "/");
}

public class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>Name of the active provider (see <see cref="SearchProviderCatalog"/>).</summary>
    public string Provider { get; set; } = "Tavily";
    /// <summary>Shorthand: used as the active provider's key when <c>Search:Providers:{Name}:ApiKey</c> is empty.</summary>
    public string ApiKey { get; set; } = "";
    public int TopResults { get; set; } = 3;
}

/// <summary>Formats provider results as Markdown chunks for prompt hydration.</summary>
public sealed class LiveSearchService(ISearchProvider provider, IOptions<SearchOptions> options) : ILiveSearchService
{
    public async Task<string> GetLiveSearchResultsAsync(string query, CancellationToken cancellationToken = default)
    {
        var top = Math.Max(1, options.Value.TopResults);
        var results = await provider.SearchAsync(query, top, cancellationToken);
        if (results.Count == 0) return "_No search results found._";

        var sb = new StringBuilder();
        foreach (var r in results.Take(top))
        {
            sb.AppendLine($"### [{r.Title}]({r.Url})");
            sb.AppendLine(r.Snippet);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

public sealed class StubLiveSearchService : ILiveSearchService
{
    public Task<string> GetLiveSearchResultsAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult("_No live search provider is configured (set `Search:Provider` and its credentials)._");
}
