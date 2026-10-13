using AgentDesk.Core.Domain;

namespace AgentDesk.Api.Endpoints;

/// <summary>
/// A KB article as returned by the API. Identical to the stored document except that the embedding vector itself is
/// left out (it is large and only useful server-side); its metadata is kept so the console can show embedding status.
/// </summary>
public sealed record KbArticleDto(
    string Id,
    string Number,
    string Title,
    string Summary,
    string Body,
    string? Symptoms,
    string? Cause,
    string? Resolution,
    string? Category,
    IReadOnlyList<string> Tags,
    string Status,
    string Visibility,
    int Version,
    IReadOnlyList<string> SourceTicketIds,
    Person Author,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    DateTimeOffset? PublishedAt,
    string? PublishedBy,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? ReviewDueAt,
    AiAssistance? AiAssisted,
    EmbeddingInfo Embedding,
    string? ETag)
{
    public static KbArticleDto From(KbArticle a) => new(
        a.Id, a.Number, a.Title, a.Summary, a.Body, a.Symptoms, a.Cause, a.Resolution, a.Category, a.Tags, a.Status,
        a.Visibility, a.Version, a.SourceTicketIds, a.Author, a.CreatedAt, a.CreatedBy, a.UpdatedAt, a.UpdatedBy,
        a.PublishedAt, a.PublishedBy, a.ArchivedAt, a.ReviewDueAt, a.AiAssisted,
        new EmbeddingInfo(a.EmbeddingStatus, a.EmbeddingProvider, a.EmbeddingModel, a.EmbeddingDimensions, a.EmbeddedAt,
            a.EmbeddingError, a.EmbeddingAttempts, a.Embedding is { Length: > 0 }),
        a.ETag);
}

public sealed record EmbeddingInfo(
    string Status,
    string? Provider,
    string? Model,
    int? Dimensions,
    DateTimeOffset? EmbeddedAt,
    string? Error,
    int Attempts,
    bool HasVector);

/// <summary>An attachment as returned by the API (the internal blob path is not exposed).</summary>
public sealed record AttachmentDto(
    string Id,
    string TicketId,
    string? CommentId,
    string FileName,
    string ContentType,
    long Size,
    string? Sha256,
    string Status,
    string? ScanResult,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public static AttachmentDto From(Attachment a) => new(
        a.Id, a.TicketId, a.CommentId, a.FileName, a.ContentType, a.Size, a.Sha256, a.Status, a.ScanResult,
        a.CreatedAt, a.CreatedBy);
}

public sealed record PageDto<T>(IReadOnlyList<T> Items, string? ContinuationToken);
