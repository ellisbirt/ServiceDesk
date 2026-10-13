using AgentDesk.Core.Domain;

namespace AgentDesk.Core.Abstractions;

/// <summary>The authenticated caller. Tenant and user ids never contain ':' (they are used in composite keys).</summary>
public sealed record CurrentUser(string TenantId, string UserId, string DisplayName, string? Email, IReadOnlySet<string> Roles)
{
    public Person AsPerson() => new(UserId, DisplayName, Email);

    public bool IsInRole(string role) => Roles.Contains(role);

    /// <summary>The identity used by background work such as the intake consumer and the embedding sweep.</summary>
    public static CurrentUser System(string tenantId) =>
        new(tenantId, "system", "AgentDesk", null, new HashSet<string> { AgentDeskRoles.Admin });
}

public static class AgentDeskRoles
{
    public const string Agent = "AgentDesk.Agent";
    public const string KbAuthor = "AgentDesk.KbAuthor";
    public const string Admin = "AgentDesk.Admin";
    /// <summary>Planned: an end user using a future requester portal.</summary>
    public const string Requester = "AgentDesk.Requester";
}

public interface ICurrentUserAccessor
{
    CurrentUser Current { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? ContinuationToken);

public sealed record TicketQuery
{
    public IReadOnlyList<string>? Statuses { get; init; }
    public string? Priority { get; init; }
    public string? AssigneeId { get; init; }
    /// <summary>When true, only tickets without an assignee.</summary>
    public bool Unassigned { get; init; }
    public string? AssignmentGroup { get; init; }
    public string? Category { get; init; }
    public string? RequesterId { get; init; }
    /// <summary>Case-insensitive substring match on number and subject.</summary>
    public string? Text { get; init; }
    public bool? Breached { get; init; }
    /// <summary>updatedAt (default), createdAt, priority, or dueAt.</summary>
    public string Sort { get; init; } = "updatedAt";
    public int PageSize { get; init; } = 25;
    public string? ContinuationToken { get; init; }
}

public sealed record KbQuery
{
    public string? Status { get; init; }
    public string? Category { get; init; }
    /// <summary>Case-insensitive substring match on number, title, summary and tags. This is keyword filtering, not RAG.</summary>
    public string? Text { get; init; }
    public int PageSize { get; init; } = 25;
    public string? ContinuationToken { get; init; }
}

public interface ITicketRepository
{
    Task<Ticket?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    /// <exception cref="DuplicateException">A ticket with the same id exists.</exception>
    Task<Ticket> CreateAsync(Ticket ticket, CancellationToken ct = default);
    /// <summary>Replaces the ticket if <paramref name="ifMatch"/> (when given) equals the stored ETag.</summary>
    /// <exception cref="ConcurrencyConflictException"/>
    Task<Ticket> ReplaceAsync(Ticket ticket, string? ifMatch, CancellationToken ct = default);
    Task<Page<Ticket>> QueryAsync(string tenantId, TicketQuery query, CancellationToken ct = default);

    Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(string tenantId, string ticketId, CancellationToken ct = default);

    /// <summary>Atomically increments the named per-tenant counter and returns the new value.</summary>
    Task<long> NextNumberAsync(string tenantId, string counterName, CancellationToken ct = default);

    Task<IntakeReceipt?> GetIntakeReceiptAsync(string tenantId, string eventId, CancellationToken ct = default);
    /// <summary>Creates the receipt; returns false if one already exists.</summary>
    Task<bool> TryCreateIntakeReceiptAsync(IntakeReceipt receipt, CancellationToken ct = default);
}

public interface IKbRepository
{
    Task<KbArticle?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<KbArticle> CreateAsync(KbArticle article, CancellationToken ct = default);
    /// <exception cref="ConcurrencyConflictException"/>
    Task<KbArticle> ReplaceAsync(KbArticle article, string? ifMatch, CancellationToken ct = default);
    Task<Page<KbArticle>> QueryAsync(string tenantId, KbQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<KbArticle>> GetManyAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    Task AddVersionAsync(KbVersion version, CancellationToken ct = default);
    Task<IReadOnlyList<KbVersion>> GetVersionsAsync(string tenantId, string articleId, CancellationToken ct = default);

    /// <summary>Articles whose embedding is pending or failed, across all tenants (used by the background sweep).</summary>
    Task<IReadOnlyList<KbArticle>> FindNeedingEmbeddingAsync(int maxAttempts, int limit, CancellationToken ct = default);
}

public interface IAttachmentRepository
{
    Task<Attachment?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<Attachment> CreateAsync(Attachment attachment, CancellationToken ct = default);
    Task<Attachment> ReplaceAsync(Attachment attachment, string? ifMatch, CancellationToken ct = default);
    Task<IReadOnlyList<Attachment>> ListForTicketAsync(string tenantId, string ticketId, CancellationToken ct = default);
}

public sealed record BlobProperties(long Size, string? ContentType, string? Sha256Hex);

public interface IBlobStore
{
    /// <summary>A short-lived URL the client can PUT the file to (requires header <c>x-ms-blob-type: BlockBlob</c>).</summary>
    Task<Uri> GetUploadUriAsync(string path, string contentType, TimeSpan lifetime, CancellationToken ct = default);
    /// <summary>A short-lived read URL. The download is served as an attachment with the given file name.</summary>
    Task<Uri> GetDownloadUriAsync(string path, string fileName, string contentType, TimeSpan lifetime, CancellationToken ct = default);
    /// <summary>Returns null if the blob does not exist. Computes SHA-256 by reading the blob when the store does not keep it.</summary>
    Task<BlobProperties?> GetPropertiesAsync(string path, bool computeSha256, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
}

/// <summary>Planned malware-scan hook. The default implementation accepts every file.</summary>
public interface IAttachmentScanner
{
    Task<(string Status, string? Result)> ScanAsync(Attachment attachment, CancellationToken ct = default);
}

/// <summary>Pushes live updates to connected agent consoles.</summary>
public interface ITicketNotifier
{
    Task TicketCreatedAsync(Ticket ticket, CancellationToken ct = default);
    Task TicketUpdatedAsync(Ticket ticket, CancellationToken ct = default);
    Task TimelineEntryAddedAsync(Ticket ticket, TimelineEntry entry, CancellationToken ct = default);
    Task KbChangedAsync(KbArticle article, CancellationToken ct = default);
}

public sealed class NullTicketNotifier : ITicketNotifier
{
    public Task TicketCreatedAsync(Ticket ticket, CancellationToken ct = default) => Task.CompletedTask;
    public Task TicketUpdatedAsync(Ticket ticket, CancellationToken ct = default) => Task.CompletedTask;
    public Task TimelineEntryAddedAsync(Ticket ticket, TimelineEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    public Task KbChangedAsync(KbArticle article, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Generates and stores the vector for a KB article. Implemented in Infrastructure on top of Helpdesk.Llm.</summary>
public interface IKbEmbeddingService
{
    /// <summary>
    /// Updates the embedding fields on <paramref name="article"/> in place (it does not save). Never throws for provider
    /// failures: it sets <see cref="KbArticle.EmbeddingStatus"/> to failed and records the error instead.
    /// </summary>
    Task ApplyAsync(KbArticle article, CancellationToken ct = default);
}
