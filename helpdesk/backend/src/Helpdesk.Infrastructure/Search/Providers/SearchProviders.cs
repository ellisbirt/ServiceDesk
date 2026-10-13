using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Helpdesk.Infrastructure.Search.Providers;

public sealed class TavilySearchProvider(HttpClient http) : ISearchProvider
{
    public static readonly SearchProviderDefinition Definition = new(
        "Tavily", "https://api.tavily.com/",
        o => !string.IsNullOrWhiteSpace(o.ApiKey),
        (http, o) => http.DefaultRequestHeaders.Authorization = new("Bearer", o.ApiKey),
        (http, _) => new TavilySearchProvider(http));

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query, ["max_results"] = maxResults, ["search_depth"] = "basic" };
        using var response = await http.PostAsJsonAsync("search", body, ct);
        response.EnsureSuccessStatusCode();
        return SearchJson.Map(await response.Content.ReadFromJsonAsync<JsonNode>(ct), "results", "title", "url", "content");
    }
}

public sealed class BraveSearchProvider(HttpClient http) : ISearchProvider
{
    public static readonly SearchProviderDefinition Definition = new(
        "Brave", "https://api.search.brave.com/",
        o => !string.IsNullOrWhiteSpace(o.ApiKey),
        (http, o) =>
        {
            http.DefaultRequestHeaders.Add("X-Subscription-Token", o.ApiKey);
            http.DefaultRequestHeaders.Add("Accept", "application/json");
        },
        (http, _) => new BraveSearchProvider(http));

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        using var response = await http.GetAsync($"res/v1/web/search?q={Uri.EscapeDataString(query)}&count={maxResults}", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
        return SearchJson.Map(json?["web"], "results", "title", "url", "description");
    }
}

/// <summary>Serper.dev: Google results as JSON.</summary>
public sealed class SerperSearchProvider(HttpClient http) : ISearchProvider
{
    public static readonly SearchProviderDefinition Definition = new(
        "Serper", "https://google.serper.dev/",
        o => !string.IsNullOrWhiteSpace(o.ApiKey),
        (http, o) => http.DefaultRequestHeaders.Add("X-API-KEY", o.ApiKey),
        (http, _) => new SerperSearchProvider(http));

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var body = new JsonObject { ["q"] = query, ["num"] = maxResults };
        using var response = await http.PostAsJsonAsync("search", body, ct);
        response.EnsureSuccessStatusCode();
        return SearchJson.Map(await response.Content.ReadFromJsonAsync<JsonNode>(ct), "organic", "title", "link", "snippet");
    }
}

/// <summary>Google Programmable Search (Custom Search JSON API). Needs an API key and a search engine id.</summary>
public sealed class GoogleSearchProvider(HttpClient http, SearchProviderOptions options) : ISearchProvider
{
    public static readonly SearchProviderDefinition Definition = new(
        "Google", "https://www.googleapis.com/",
        o => !string.IsNullOrWhiteSpace(o.ApiKey) && !string.IsNullOrWhiteSpace(o.SearchEngineId),
        (_, _) => { },
        (http, o) => new GoogleSearchProvider(http, o));

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var num = Math.Clamp(maxResults, 1, 10);
        var url = $"customsearch/v1?key={Uri.EscapeDataString(options.ApiKey)}&cx={Uri.EscapeDataString(options.SearchEngineId)}" +
                  $"&q={Uri.EscapeDataString(query)}&num={num}";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return SearchJson.Map(await response.Content.ReadFromJsonAsync<JsonNode>(ct), "items", "title", "link", "snippet");
    }
}

/// <summary>Self-hosted SearXNG. The instance must have the <c>json</c> output format enabled.</summary>
public sealed class SearxngSearchProvider(HttpClient http) : ISearchProvider
{
    public static readonly SearchProviderDefinition Definition = new(
        "SearXNG", "",
        o => !string.IsNullOrWhiteSpace(o.BaseUrl),
        (_, _) => { },
        (http, _) => new SearxngSearchProvider(http));

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        using var response = await http.GetAsync($"search?q={Uri.EscapeDataString(query)}&format=json", ct);
        response.EnsureSuccessStatusCode();
        var all = SearchJson.Map(await response.Content.ReadFromJsonAsync<JsonNode>(ct), "results", "title", "url", "content");
        return all.Take(maxResults).ToList();
    }
}

internal static class SearchJson
{
    /// <summary>Maps an array of result objects using the given field names, skipping entries without a URL.</summary>
    public static IReadOnlyList<SearchResult> Map(JsonNode? root, string arrayName, string title, string url, string snippet) =>
        (root?[arrayName]?.AsArray() ?? [])
            .Select(r => new SearchResult((string?)r?[title] ?? "", (string?)r?[url] ?? "", (string?)r?[snippet] ?? ""))
            .Where(r => r.Url.Length > 0)
            .ToList();
}
