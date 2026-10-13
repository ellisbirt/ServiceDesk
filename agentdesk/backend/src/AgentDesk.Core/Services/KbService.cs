using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using AgentDesk.Core.Rules;
using Microsoft.Extensions.Logging;

namespace AgentDesk.Core.Services;

public static class KbContent
{
    public const int MaxTitle = 200;
    public const int MaxSummary = 1000;
    public const int MaxBody = 50_000;
    public const int MaxSection = 10_000;

    /// <summary>The text that is embedded: every content field, labelled, in a fixed order.</summary>
    public static string EmbeddingText(KbArticle a)
    {
        var sb = new StringBuilder();
        Append(sb, "Title", a.Title);
        Append(sb, "Summary", a.Summary);
        Append(sb, "Symptoms", a.Symptoms);
        Append(sb, "Cause", a.Cause);
        Append(sb, "Resolution", a.Resolution);
        Append(sb, "Article", a.Body);
        if (a.Tags.Count > 0) Append(sb, "Tags", string.Join(", ", a.Tags));
        return sb.ToString().TrimEnd();
    }

    public static string Hash(KbArticle a) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddingText(a))));

    private static void Append(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(label).Append(": ").AppendLine(value.Trim()).AppendLine();
    }
}

public sealed class KbService(
    IKbRepository repository,
    ITicketRepository tickets,
    IKbEmbeddingService embeddings,
    ITicketNotifier notifier,
    IClock clock,
    ILogger<KbService> logger)
{
    public const string CounterName = "kb";

    public async Task<KbArticle> GetAsync(CurrentUser user, string id, CancellationToken ct = default) =>
        await repository.GetAsync(user.TenantId, id, ct) ?? throw new NotFoundException("Knowledge article", id);

    public Task<Page<KbArticle>> QueryAsync(CurrentUser user, KbQuery query, CancellationToken ct = default)
    {
        new Validator()
            .OneOf("status", query.Status, KbStatuses.All, optional: true)
            .Range("pageSize", query.PageSize, 1, 100)
            .ThrowIfInvalid();
        return repository.QueryAsync(user.TenantId, query, ct);
    }

    public async Task<IReadOnlyList<KbVersion>> GetVersionsAsync(CurrentUser user, string id, CancellationToken ct = default)
    {
        await GetAsync(user, id, ct);
        return await repository.GetVersionsAsync(user.TenantId, id, ct);
    }

    public async Task<KbArticle> CreateAsync(CurrentUser user, KbArticleInput input, CancellationToken ct = default)
    {
        Validate(input);
        var now = clock.UtcNow;
        var number = await tickets.NextNumberAsync(user.TenantId, CounterName, ct);
        var article = new KbArticle
        {
            Id = TenantDocument.NewId(),
            TenantId = user.TenantId,
            Number = Numbering.Kb(number),
            Author = user.AsPerson(),
            CreatedAt = now,
            CreatedBy = user.UserId,
            UpdatedAt = now,
            UpdatedBy = user.UserId,
            Version = 1,
            Status = KbStatuses.Draft,
            AiAssisted = input.AiAssisted
        };
        ApplyContent(article, input);
        article.SourceTicketIds = Distinct(input.SourceTicketIds);
        await embeddings.ApplyAsync(article, ct);

        var created = await repository.CreateAsync(article, ct);
        await repository.AddVersionAsync(Snapshot(created), ct);
        await notifier.KbChangedAsync(created, ct);
        logger.LogInformation("Created KB article {KbNumber} ({KbId}), embedding {EmbeddingStatus}",
            created.Number, created.Id, created.EmbeddingStatus);
        return created;
    }

    public async Task<KbArticle> UpdateAsync(CurrentUser user, string id, KbArticleInput input, string? ifMatch, CancellationToken ct = default)
    {
        Validate(input);
        var article = await GetAsync(user, id, ct);
        if (article.Status == KbStatuses.Archived)
            throw new InvalidTransitionException("Archived articles cannot be edited. Restore it to a draft first.");

        var before = KbContent.Hash(article);
        ApplyContent(article, input);
        foreach (var ticketId in input.SourceTicketIds ?? [])
            if (!article.SourceTicketIds.Contains(ticketId)) article.SourceTicketIds.Add(ticketId);
        var changed = KbContent.Hash(article) != before;
        if (changed) article.Version++;
        Touch(article, user);
        await embeddings.ApplyAsync(article, ct);

        var saved = await repository.ReplaceAsync(article, ifMatch, ct);
        if (changed) await repository.AddVersionAsync(Snapshot(saved), ct);
        await notifier.KbChangedAsync(saved, ct);
        return saved;
    }

    public async Task<KbArticle> PublishAsync(CurrentUser user, string id, string? ifMatch, CancellationToken ct = default)
    {
        var article = await GetAsync(user, id, ct);
        if (article.Status == KbStatuses.Published) return article;
        var published = await PublishCoreAsync(user, article, ifMatch, ct);
        await notifier.KbChangedAsync(published, ct);
        return published;
    }

    public async Task<KbArticle> ArchiveAsync(CurrentUser user, string id, string? ifMatch, CancellationToken ct = default)
    {
        var article = await GetAsync(user, id, ct);
        if (article.Status == KbStatuses.Archived) return article;
        article.Status = KbStatuses.Archived;
        article.ArchivedAt = clock.UtcNow;
        Touch(article, user);
        var saved = await repository.ReplaceAsync(article, ifMatch, ct);
        await notifier.KbChangedAsync(saved, ct);
        return saved;
    }

    /// <summary>Moves an archived article back to draft so it can be edited and published again.</summary>
    public async Task<KbArticle> RestoreAsync(CurrentUser user, string id, string? ifMatch, CancellationToken ct = default)
    {
        var article = await GetAsync(user, id, ct);
        if (article.Status != KbStatuses.Archived) throw new InvalidTransitionException("Only archived articles can be restored.");
        article.Status = KbStatuses.Draft;
        article.ArchivedAt = null;
        Touch(article, user);
        var saved = await repository.ReplaceAsync(article, ifMatch, ct);
        await notifier.KbChangedAsync(saved, ct);
        return saved;
    }

    /// <summary>
    /// Loads the cited articles for a reply. Drafts are published when <paramref name="publishDrafts"/> is true (a public reply
    /// is sending them to the requester). Archived articles cannot be cited.
    /// </summary>
    internal async Task<(IReadOnlyList<KbArticle> Articles, IReadOnlyList<KbArticle> Published)> ResolveCitationsAsync(
        CurrentUser user, IReadOnlyCollection<string> ids, bool publishDrafts, string ticketId, CancellationToken ct)
    {
        if (ids.Count == 0) return ([], []);
        var found = await repository.GetManyAsync(user.TenantId, ids, ct);
        var missing = ids.Except(found.Select(a => a.Id)).ToList();
        if (missing.Count > 0)
            throw ValidationException.For("citedKbArticleIds", $"Unknown knowledge article(s): {string.Join(", ", missing)}.");
        if (found.FirstOrDefault(a => a.Status == KbStatuses.Archived) is { } archived)
            throw ValidationException.For("citedKbArticleIds", $"{archived.Number} is archived and cannot be cited.");

        var articles = new List<KbArticle>();
        var published = new List<KbArticle>();
        foreach (var article in found)
        {
            var current = article;
            if (publishDrafts && article.Status == KbStatuses.Draft)
            {
                if (!article.SourceTicketIds.Contains(ticketId)) article.SourceTicketIds.Add(ticketId);
                current = await PublishCoreAsync(user, article, ifMatch: null, ct);
                published.Add(current);
            }
            articles.Add(current);
        }
        return (articles, published);
    }

    /// <summary>Used by the background sweep to retry pending or failed embeddings.</summary>
    public async Task<KbArticle> ReembedAsync(KbArticle article, CancellationToken ct = default)
    {
        await embeddings.ApplyAsync(article, ct);
        return await repository.ReplaceAsync(article, article.ETag, ct);
    }

    private async Task<KbArticle> PublishCoreAsync(CurrentUser user, KbArticle article, string? ifMatch, CancellationToken ct)
    {
        var now = clock.UtcNow;
        article.Status = KbStatuses.Published;
        article.PublishedAt = now;
        article.PublishedBy = user.UserId;
        article.ArchivedAt = null;
        article.ReviewDueAt ??= now.AddMonths(12);
        Touch(article, user);
        var saved = await repository.ReplaceAsync(article, ifMatch, ct);
        logger.LogInformation("Published KB article {KbNumber} v{Version}", saved.Number, saved.Version);
        return saved;
    }

    private void Touch(KbArticle article, CurrentUser user)
    {
        article.UpdatedAt = clock.UtcNow;
        article.UpdatedBy = user.UserId;
    }

    private static void ApplyContent(KbArticle article, KbArticleInput input)
    {
        article.Title = input.Title.Trim();
        article.Summary = input.Summary.Trim();
        article.Body = input.Body;
        article.Symptoms = Blank(input.Symptoms);
        article.Cause = Blank(input.Cause);
        article.Resolution = Blank(input.Resolution);
        article.Category = Blank(input.Category);
        article.Tags = Distinct(input.Tags?.Select(t => t.Trim().ToLowerInvariant()));
        if (input.Visibility is not null) article.Visibility = input.Visibility;
        if (input.AiAssisted is not null) article.AiAssisted = input.AiAssisted;
    }

    private static void Validate(KbArticleInput input) =>
        new Validator()
            .Required("title", input.Title, KbContent.MaxTitle)
            .Required("summary", input.Summary, KbContent.MaxSummary)
            .Required("body", input.Body, KbContent.MaxBody)
            .MaxLength("symptoms", input.Symptoms, KbContent.MaxSection)
            .MaxLength("cause", input.Cause, KbContent.MaxSection)
            .MaxLength("resolution", input.Resolution, KbContent.MaxSection)
            .OneOf("category", input.Category, TicketCategories.All, optional: true)
            .OneOf("visibility", input.Visibility, Visibilities.All, optional: true)
            .When(input.Tags is { Count: > 20 }, "tags", "must contain at most 20 tags.")
            .ThrowIfInvalid();

    private static KbVersion Snapshot(KbArticle a) => new()
    {
        Id = KbVersion.IdFor(a.Id, a.Version),
        TenantId = a.TenantId,
        ArticleId = a.Id,
        Version = a.Version,
        Status = a.Status,
        Title = a.Title,
        Summary = a.Summary,
        Body = a.Body,
        Symptoms = a.Symptoms,
        Cause = a.Cause,
        Resolution = a.Resolution,
        Category = a.Category,
        Tags = [.. a.Tags],
        CreatedAt = a.UpdatedAt,
        CreatedBy = a.UpdatedBy,
        UpdatedAt = a.UpdatedAt,
        UpdatedBy = a.UpdatedBy
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> Distinct(IEnumerable<string>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList() ?? [];
}
