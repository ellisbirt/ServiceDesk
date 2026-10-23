using System.Diagnostics;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Kb;

/// <summary>
/// Hybrid retrieval over the knowledge base.
/// <list type="bullet">
/// <item><b>Semantic:</b> the query is embedded (after DLP redaction for the Embedding destination) and compared with the
/// stored article vectors, which finds the right article when the wording differs ("can't print" and "printer offline").</item>
/// <item><b>Lexical:</b> BM25 over the tenant's published articles, which finds exact identifiers (an error code, a product
/// name, a KB number) that embeddings blur, and still works when no embedding model is configured or it is down.</item>
/// </list>
/// The two rankings are fused with reciprocal rank fusion. Retrieval is best effort: any failure, or a timeout, returns no
/// articles and the answer goes ahead without them.
/// </summary>
public sealed class KnowledgeBaseSearch(
    IKbSource source,
    ILlmProviderRouter router,
    ISensitiveDataRedactor redactor,
    IMemoryCache cache,
    TimeProvider time,
    IOptions<KbOptions> options,
    ILogger<KnowledgeBaseSearch> logger) : IKnowledgeBaseSearch
{
    private static readonly TimeSpan SemanticBackoff = TimeSpan.FromMinutes(1);
    private long _semanticDisabledUntil;

    public async Task<IReadOnlyList<KbHit>> SearchAsync(string tenantId, string query, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var started = Stopwatch.GetTimestamp();
        var mode = "lexical";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        try
        {
            var text = query.Length <= o.MaxQueryChars ? query : query[^o.MaxQueryChars..];
            var index = await GetIndexAsync(tenantId, timeout.Token);
            if (index.Count == 0)
            {
                Record("miss", mode, started);
                return [];
            }

            var pool = Math.Max(o.TopResults, o.CandidatePool);
            var lexical = index.Search(text, pool);
            var semantic = await SemanticAsync(tenantId, text, index, pool, timeout.Token);
            if (semantic is not null) mode = "hybrid";

            var fused = HybridRanker.Fuse(
                semantic?.Select(m => m.Id).ToList() ?? [], lexical.Select(l => l.Doc.Id).ToList(), o.RrfK, o.TopResults);
            var hits = fused
                .Select(f => (Doc: index.Find(f.Id), Fused: f))
                .Where(f => f.Doc is not null)
                .Select(f => ToHit(f.Doc!, f.Fused, o.MaxExcerptChars))
                .ToList();

            Record(hits.Count > 0 ? "hit" : "miss", mode, started);
            return hits;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes the retrieval timeout. The KB only ever improves an answer, so it never fails one.
            logger.LogWarning(ex, "Knowledge-base retrieval failed for Tenant: {TenantId}; answering without it", tenantId);
            Record("error", mode, started);
            return [];
        }
    }

    /// <summary>The semantic ranking, or null when it could not run (no embedding model, backoff, or a failure).</summary>
    private async Task<IReadOnlyList<KbVectorMatch>?> SemanticAsync(
        string tenantId, string query, KbLexicalIndex index, int pool, CancellationToken ct)
    {
        var o = options.Value;
        if (!router.HasRole(o.EmbeddingRole)) return null;
        if (time.GetTimestamp() < Interlocked.Read(ref _semanticDisabledUntil)) return null;

        try
        {
            var provider = router.GetEmbedding(o.EmbeddingRole);
            // The Embedding policy is stricter than the Model one for some values, so redact again for this destination.
            var embeddable = redactor.Redact(query, EgressDestination.Embedding, tenantId).Text;
            var vectors = await provider.EmbedAsync([embeddable], ct);
            if (vectors.Count != 1 || vectors[0].Length == 0) throw new InvalidOperationException("The embedding provider returned no vector.");

            var matches = await source.VectorSearchAsync(tenantId, vectors[0], provider.Provider, provider.Model, pool, ct);
            // The index can be up to a cache lifetime behind the store; an article it does not know yet is skipped.
            return matches.Where(m => m.Similarity >= o.MinSimilarity && index.Find(m.Id) is not null).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Typically the account has no vector search, or the embedding model is down. Keep serving lexical results
            // and stop retrying the semantic leg for a minute instead of paying for a failure on every message.
            Interlocked.Exchange(ref _semanticDisabledUntil, time.GetTimestamp() + (long)(SemanticBackoff.TotalSeconds * time.TimestampFrequency));
            logger.LogWarning(ex, "Semantic KB search is unavailable for Tenant: {TenantId}; using lexical search only for a while", tenantId);
            return null;
        }
    }

    private Task<KbLexicalIndex> GetIndexAsync(string tenantId, CancellationToken ct)
    {
        var o = options.Value;
        return cache.GetOrCreateAsync($"kb-index:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, o.CorpusCacheSeconds));
            var documents = await source.LoadPublishedAsync(tenantId, o.MaxArticles, ct);
            return documents.Count == 0 ? KbLexicalIndex.Empty : new KbLexicalIndex(documents);
        })!;
    }

    private static KbHit ToHit(KbDocument doc, HybridRanker.Fused fused, int maxExcerpt) =>
        new(doc.Id, doc.Number, doc.Title, doc.Summary, Excerpt(doc, maxExcerpt), fused.Score, fused.Match);

    /// <summary>The resolution is what answers a question, so it comes first; the body only fills the remainder.</summary>
    public static string Excerpt(KbDocument doc, int max)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.Symptoms)) parts.Add($"Symptoms: {doc.Symptoms.Trim()}");
        if (!string.IsNullOrWhiteSpace(doc.Cause)) parts.Add($"Cause: {doc.Cause.Trim()}");
        if (!string.IsNullOrWhiteSpace(doc.Resolution)) parts.Add($"Resolution: {doc.Resolution.Trim()}");
        else if (!string.IsNullOrWhiteSpace(doc.Body)) parts.Add(doc.Body.Trim());

        var text = string.Join("\n", parts);
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static void Record(string outcome, string mode, long started)
    {
        Telemetry.KbSearches.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("mode", mode));
        Telemetry.KbSearchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
    }
}

/// <summary>Used when no KB provider is configured: every search finds nothing.</summary>
public sealed class DisabledKnowledgeBaseSearch : IKnowledgeBaseSearch
{
    public Task<IReadOnlyList<KbHit>> SearchAsync(string tenantId, string query, CancellationToken cancellationToken = default)
    {
        Telemetry.KbSearches.Add(1, new KeyValuePair<string, object?>("outcome", "disabled"));
        return Task.FromResult<IReadOnlyList<KbHit>>([]);
    }
}
