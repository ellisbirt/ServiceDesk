using System.Text.Json.Serialization;

namespace AgentDesk.Core.Domain;

/// <summary>
/// Fields shared by every stored document. All containers are partitioned by <see cref="TenantId"/>, so every read and
/// write is scoped to one tenant. <see cref="ETag"/> supports optimistic concurrency (HTTP If-Match).
/// </summary>
public abstract class TenantDocument
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public abstract string DocType { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>A person as shown on a ticket. Display data is copied in so lists render without a directory lookup.</summary>
public sealed record Person(string Id, string DisplayName, string? Email = null);

public sealed record TicketSource(string? EventId = null, string? ConversationId = null, string? CorrelationId = null);

public sealed record LinkedTicket(string TicketId, string Number, string Relation);

public sealed class SlaState
{
    public string PolicyId { get; set; } = "default";
    public DateTimeOffset FirstResponseDueAt { get; set; }
    public DateTimeOffset ResolutionDueAt { get; set; }
    public DateTimeOffset? FirstRespondedAt { get; set; }
    public bool FirstResponseBreached { get; set; }
    public bool ResolutionBreached { get; set; }
}

public sealed class Resolution
{
    public string Code { get; set; } = "";
    public string? Notes { get; set; }
    public DateTimeOffset ResolvedAt { get; set; }
    public string ResolvedBy { get; set; } = "";
}

public sealed class Ticket : TenantDocument
{
    public override string DocType { get; set; } = DocTypes.Ticket;
    public string Number { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Operator-facing summary of the requester's chat before the ticket was raised. Kept apart from <see cref="Description"/>; null for tickets that did not come from a chat.</summary>
    public string? ConversationSummary { get; set; }
    /// <summary>incident | service_request | question | problem.</summary>
    public string Type { get; set; } = TicketTypes.Incident;
    public string Status { get; set; } = TicketStatuses.New;
    public string? PendingReason { get; set; }
    /// <summary>1 (high) to 3 (low).</summary>
    public int Impact { get; set; } = 3;
    /// <summary>1 (high) to 3 (low).</summary>
    public int Urgency { get; set; } = 3;
    public string Priority { get; set; } = Priorities.P4;
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
    public List<string> Tags { get; set; } = [];
    public Person Requester { get; set; } = new("", "");
    public string? AssignmentGroup { get; set; }
    public Person? Assignee { get; set; }
    public List<Person> Watchers { get; set; } = [];
    public string Channel { get; set; } = Channels.Agent;
    public TicketSource? Source { get; set; }
    public List<LinkedTicket> LinkedTickets { get; set; } = [];
    public List<string> KbArticleIds { get; set; } = [];
    public SlaState Sla { get; set; } = new();
    public Resolution? Resolution { get; set; }
    public int AttachmentCount { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
}

public sealed record CitedKbArticle(string Id, string Number, int Version, string Title);

/// <summary>Records that an AI feature produced the first draft of a comment.</summary>
public sealed record AiAssistance(string Feature, string Provider, string Model, string PromptVersion);

public sealed record FieldChange(string Field, string? From, string? To);

/// <summary>
/// A timeline entry stored in the tickets container next to its ticket (same partition): either a <c>comment</c>
/// (conversation) or an <c>event</c> (audit trail of changes).
/// </summary>
public sealed class TimelineEntry : TenantDocument
{
    public override string DocType { get; set; } = DocTypes.Comment;
    public string TicketId { get; set; } = "";
    public Person Author { get; set; } = new("", "");

    // Comment fields.
    public string? Visibility { get; set; }
    public string? Body { get; set; }
    public List<string> AttachmentIds { get; set; } = [];
    public List<CitedKbArticle> CitedKbArticles { get; set; } = [];
    public AiAssistance? AiAssisted { get; set; }

    // Event fields.
    public string? EventType { get; set; }
    public List<FieldChange> Changes { get; set; } = [];
    public string? Note { get; set; }
}

/// <summary>A per-tenant sequence used for human-readable numbers. Incremented atomically.</summary>
public sealed class Counter : TenantDocument
{
    public override string DocType { get; set; } = DocTypes.Counter;
    public long Value { get; set; }

    public static string IdFor(string name) => $"counter-{name}";
}

/// <summary>Marks an intake event as processed so a redelivered queue message does not create a second ticket.</summary>
public sealed class IntakeReceipt : TenantDocument
{
    public override string DocType { get; set; } = DocTypes.IntakeReceipt;
    public string EventId { get; set; } = "";
    public string? TicketId { get; set; }
    /// <summary>Seconds until Cosmos DB deletes the receipt (container default TTL must be enabled, e.g. -1).</summary>
    [JsonPropertyName("ttl")] public int? Ttl { get; set; }

    public static string IdFor(string eventId) => $"intake-{eventId}";
}

public sealed class KbArticle : TenantDocument
{
    public override string DocType { get; set; } = DocTypes.KbArticle;
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Symptoms { get; set; }
    public string? Cause { get; set; }
    public string? Resolution { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Status { get; set; } = KbStatuses.Draft;
    public string Visibility { get; set; } = Visibilities.Internal;
    public int Version { get; set; } = 1;
    public List<string> SourceTicketIds { get; set; } = [];
    public Person Author { get; set; } = new("", "");
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? ReviewDueAt { get; set; }
    public AiAssistance? AiAssisted { get; set; }

    // Embedding: stored alongside the article for future retrieval. No retrieval is implemented yet.
    public float[]? Embedding { get; set; }
    public string? EmbeddingProvider { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? EmbeddingDimensions { get; set; }
    public string? EmbeddingContentHash { get; set; }
    public DateTimeOffset? EmbeddedAt { get; set; }
    public string EmbeddingStatus { get; set; } = EmbeddingStatuses.Pending;
    public string? EmbeddingError { get; set; }
    public int EmbeddingAttempts { get; set; }
}

/// <summary>An immutable snapshot of an article's content at one version.</summary>
public sealed class KbVersion : TenantDocument
{
    public override string DocType { get; set; } = DocTypes.KbVersion;
    public string ArticleId { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Symptoms { get; set; }
    public string? Cause { get; set; }
    public string? Resolution { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = [];

    public static string IdFor(string articleId, int version) => $"{articleId}-v{version}";
}

public sealed class Attachment : TenantDocument
{
    public override string DocType { get; set; } = DocTypes.Attachment;
    public string TicketId { get; set; } = "";
    public string? CommentId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Lower-case hex SHA-256 the client declared; verified against the uploaded blob on completion.</summary>
    public string? Sha256 { get; set; }
    public string BlobPath { get; set; } = "";
    public string Status { get; set; } = AttachmentStatuses.PendingUpload;
    public string? ScanResult { get; set; }
    public Person UploadedBy { get; set; } = new("", "");

    public static string PathFor(string tenantId, string ticketId, string attachmentId) => $"{tenantId}/{ticketId}/{attachmentId}";
}
