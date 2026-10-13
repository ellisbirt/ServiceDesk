using AgentDesk.Core.Domain;

namespace AgentDesk.Core.Services;

public sealed record CreateTicketCommand
{
    public string Subject { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Type { get; init; }
    public int? Impact { get; init; }
    public int? Urgency { get; init; }
    public string? Category { get; init; }
    public string? Subcategory { get; init; }
    public List<string>? Tags { get; init; }
    /// <summary>Defaults to the caller when omitted.</summary>
    public Person? Requester { get; init; }
    public string? AssignmentGroup { get; init; }
    public Person? Assignee { get; init; }
    public string? Channel { get; init; }
    public TicketSource? Source { get; init; }
}

/// <summary>Partial update: null fields are left unchanged.</summary>
public sealed record UpdateTicketCommand
{
    public string? Subject { get; init; }
    public string? Description { get; init; }
    public string? Type { get; init; }
    public int? Impact { get; init; }
    public int? Urgency { get; init; }
    public string? Category { get; init; }
    public string? Subcategory { get; init; }
    public List<string>? Tags { get; init; }
    public List<Person>? Watchers { get; init; }
}

public sealed record AssignTicketCommand(string? AssignmentGroup, Person? Assignee);

public sealed record TransitionTicketCommand(string Status, string? PendingReason = null, string? ResolutionCode = null, string? Notes = null);

public sealed record AddCommentCommand
{
    public string Visibility { get; init; } = Visibilities.Public;
    public string Body { get; init; } = "";
    public List<string>? AttachmentIds { get; init; }
    /// <summary>KB article ids cited by the reply. Cited drafts are published as part of sending a public reply.</summary>
    public List<string>? CitedKbArticleIds { get; init; }
    public AiAssistance? AiAssisted { get; init; }
    /// <summary>Optional status change applied after the comment (for example reply and resolve).</summary>
    public TransitionTicketCommand? Transition { get; init; }
}

public sealed record CommentResult(Ticket Ticket, TimelineEntry Comment, IReadOnlyList<KbArticle> PublishedArticles);

public sealed record KbArticleInput
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Body { get; init; } = "";
    public string? Symptoms { get; init; }
    public string? Cause { get; init; }
    public string? Resolution { get; init; }
    public string? Category { get; init; }
    public List<string>? Tags { get; init; }
    public string? Visibility { get; init; }
    public List<string>? SourceTicketIds { get; init; }
    public AiAssistance? AiAssisted { get; init; }
}

public sealed record CreateAttachmentCommand(string FileName, string ContentType, long Size, string? Sha256);

public sealed record AttachmentUpload(Attachment Attachment, Uri UploadUri, DateTimeOffset ExpiresAt);

public sealed record AttachmentDownload(Uri DownloadUri, DateTimeOffset ExpiresAt);
