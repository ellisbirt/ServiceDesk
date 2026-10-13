using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using Helpdesk.Dlp;
using Helpdesk.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentDesk.Infrastructure.Ai;

public sealed class KbEmbeddingOptions
{
    public const string Section = "Kb:Embedding";

    /// <summary>The <c>Llm:Roles</c> entry that supplies the embedding provider.</summary>
    public string Role { get; set; } = "embedding";
    /// <summary>Text longer than this is truncated before embedding (roughly 8k tokens for text-embedding-3-small).</summary>
    public int MaxInputChars { get; set; } = 24_000;
    public int MaxAttempts { get; set; } = 5;
    public bool SweepEnabled { get; set; } = true;
    public int SweepIntervalSeconds { get; set; } = 60;
    public int SweepBatchSize { get; set; } = 20;
}

/// <summary>
/// Generates the vector for a KB article through the pluggable <see cref="ILlmProviderRouter"/>. Text is DLP-redacted
/// for <see cref="EgressDestination.Embedding"/> first. Unchanged content with the same model is not re-embedded.
/// </summary>
public sealed class KbEmbeddingService(
    ILlmProviderRouter router,
    ISensitiveDataRedactor redactor,
    IClock clock,
    IOptions<KbEmbeddingOptions> options,
    ILogger<KbEmbeddingService> logger) : IKbEmbeddingService
{
    public async Task ApplyAsync(KbArticle article, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!router.HasRole(o.Role))
        {
            article.EmbeddingStatus = EmbeddingStatuses.Disabled;
            article.EmbeddingError = null;
            return;
        }

        IEmbeddingProvider provider;
        try
        {
            provider = router.GetEmbedding(o.Role);
        }
        catch (Exception ex) when (ex is InvalidOperationException or LlmCapabilityException)
        {
            Fail(article, $"Embedding provider is misconfigured: {ex.Message}");
            logger.LogWarning(ex, "Embedding role {Role} could not be resolved", o.Role);
            return;
        }

        var text = BuildInput(article, o.MaxInputChars);
        var redacted = redactor.Redact(text, EgressDestination.Embedding, article.TenantId).Text;
        var hash = Hash(redacted);

        if (article.EmbeddingStatus == EmbeddingStatuses.Ready && article.Embedding is { Length: > 0 }
            && article.EmbeddingContentHash == hash && article.EmbeddingModel == provider.Model
            && article.EmbeddingProvider == provider.Provider)
            return;

        try
        {
            var vectors = await provider.EmbedAsync([redacted], ct);
            if (vectors.Count != 1 || vectors[0].Length == 0)
                throw new InvalidOperationException("The embedding provider returned no vector.");

            article.Embedding = vectors[0];
            article.EmbeddingProvider = provider.Provider;
            article.EmbeddingModel = provider.Model;
            article.EmbeddingDimensions = vectors[0].Length;
            article.EmbeddingContentHash = hash;
            article.EmbeddedAt = clock.UtcNow;
            article.EmbeddingStatus = EmbeddingStatuses.Ready;
            article.EmbeddingError = null;
            article.EmbeddingAttempts = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(article, ex is HttpRequestException or UpstreamRateLimitedException or TaskCanceledException
                ? "The embedding provider is unavailable; it will be retried."
                : "Embedding failed; it will be retried.");
            logger.LogWarning(ex, "Embedding failed for KB article {ArticleId} (attempt {Attempt})", article.Id, article.EmbeddingAttempts);
        }
    }

    private static void Fail(KbArticle article, string error)
    {
        article.EmbeddingStatus = EmbeddingStatuses.Failed;
        article.EmbeddingError = error;
        article.EmbeddingAttempts++;
    }

    /// <summary>The text that represents the article for similarity: title, summary, structured sections, body and tags.</summary>
    internal static string BuildInput(KbArticle a, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine(a.Title);
        if (!string.IsNullOrWhiteSpace(a.Summary)) sb.AppendLine(a.Summary);
        if (!string.IsNullOrWhiteSpace(a.Symptoms)) sb.Append("Symptoms: ").AppendLine(a.Symptoms);
        if (!string.IsNullOrWhiteSpace(a.Cause)) sb.Append("Cause: ").AppendLine(a.Cause);
        if (!string.IsNullOrWhiteSpace(a.Resolution)) sb.Append("Resolution: ").AppendLine(a.Resolution);
        if (!string.IsNullOrWhiteSpace(a.Body)) sb.AppendLine(a.Body);
        if (a.Tags.Count > 0) sb.Append("Tags: ").AppendLine(string.Join(", ", a.Tags));
        var text = sb.ToString().Trim();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Retries embeddings that are pending or failed (for example because the provider was down when the article was saved).</summary>
public sealed class KbEmbeddingSweep(
    IServiceScopeFactory scopes,
    IOptions<KbEmbeddingOptions> options,
    ILogger<KbEmbeddingSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.SweepEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, o.SweepIntervalSeconds)));
        do
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "KB embedding sweep failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var scope = scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IKbRepository>();
        var embedder = scope.ServiceProvider.GetRequiredService<IKbEmbeddingService>();
        var candidates = await repo.FindNeedingEmbeddingAsync(o.MaxAttempts, o.SweepBatchSize, ct);
        var done = 0;
        foreach (var listed in candidates)
        {
            // List queries omit the vector; reload the full document for its ETag and content.
            var article = await repo.GetAsync(listed.TenantId, listed.Id, ct);
            if (article is null || article.EmbeddingStatus is EmbeddingStatuses.Ready or EmbeddingStatuses.Disabled) continue;
            var etag = article.ETag;
            await embedder.ApplyAsync(article, ct);
            if (article.EmbeddingStatus == EmbeddingStatuses.Disabled) return done;
            try
            {
                await repo.ReplaceAsync(article, etag, ct);
                if (article.EmbeddingStatus == EmbeddingStatuses.Ready) done++;
            }
            catch (ConcurrencyConflictException)
            {
                // Edited meanwhile; the edit re-ran the embedding or left it pending for the next sweep.
            }
        }
        if (done > 0) logger.LogInformation("KB embedding sweep embedded {Count} article(s)", done);
        return done;
    }
}
