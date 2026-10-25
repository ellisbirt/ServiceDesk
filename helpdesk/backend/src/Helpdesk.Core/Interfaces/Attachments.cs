using Helpdesk.Llm;

namespace Helpdesk.Core.Interfaces;

/// <summary>A file stored for a conversation. <see cref="BlobPath"/> is inside the container shared with the ticket worker.</summary>
public sealed record AttachmentInfo(
    Guid Id, string FileName, string ContentType, string Kind, long Size, string Sha256, string BlobPath, string ScanStatus,
    DateTimeOffset CreatedUtc);

/// <summary>
/// What the model is shown for the attached files of one request: wrapped text for the prompt, images and scanned PDFs as
/// content parts, and a rough token estimate used when choosing the model tier.
/// </summary>
/// <param name="Text">Untrusted-data blocks, one per file, to put in the prompt. Already redacted for the model.</param>
/// <param name="Parts">Images and scanned PDFs to send natively, for providers that can read them.</param>
/// <param name="EstimatedTokens">About how many input tokens <see cref="Text"/> and <see cref="Parts"/> add.</param>
/// <param name="Files">The files this describes, in order.</param>
public sealed record AttachmentContext(
    string Text, IReadOnlyList<ChatContentPart> Parts, int EstimatedTokens, IReadOnlyList<AttachmentInfo> Files)
{
    public static readonly AttachmentContext Empty = new("", [], 0, []);
    public bool IsEmpty => Files.Count == 0;
}

/// <param name="Model">What to show the model: the current message's files in full, earlier ones as short excerpts.</param>
/// <param name="Current">The files on the current message.</param>
/// <param name="All">Every file in the conversation that was resolved, for the ticket.</param>
public sealed record ResolvedAttachments(AttachmentContext Model, IReadOnlyList<AttachmentInfo> Current, IReadOnlyList<AttachmentInfo> All)
{
    public static readonly ResolvedAttachments None = new(AttachmentContext.Empty, [], []);
}

/// <summary>The limits that apply to uploads, as sent to the browser.</summary>
public sealed record AttachmentLimits(
    bool Enabled, long MaxFileBytes, int MaxFilesPerMessage, int MaxFilesPerConversation, long MaxTotalBytesPerConversation,
    IReadOnlyList<string> AllowedExtensions, int PasteMinChars, int PasteMinLines);

/// <summary>An upload was refused. <see cref="Code"/> is stable for clients; <see cref="Status"/> is the HTTP status.</summary>
public sealed class AttachmentException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface IAttachmentService
{
    /// <summary>False when no storage is configured: uploads are refused and messages carry no files.</summary>
    bool Enabled { get; }

    AttachmentLimits Limits { get; }

    /// <summary>Checks, extracts and stores an upload. Throws <see cref="AttachmentException"/> when it is refused.</summary>
    Task<AttachmentInfo> UploadAsync(
        string tenantId, string userId, Guid conversationId, string fileName, string? contentType, Stream content,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string tenantId, string userId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the files named by ids the caller has already verified. Files that are missing or belong to someone else
    /// are left out. <paramref name="earlierIds"/> are shown to the model only as short excerpts.
    /// </summary>
    Task<ResolvedAttachments> ResolveAsync(
        string tenantId, string userId, Guid conversationId, IReadOnlyList<Guid> currentIds, IReadOnlyList<Guid> earlierIds,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the files as belonging to a raised ticket so cleanup keeps them.</summary>
    Task MarkRaisedAsync(
        string tenantId, Guid conversationId, IReadOnlyList<Guid> attachmentIds, Guid ticketEventId,
        CancellationToken cancellationToken = default);
}

/// <summary>Used when attachments are not configured.</summary>
public sealed class NullAttachmentService : IAttachmentService
{
    public bool Enabled => false;

    public AttachmentLimits Limits { get; } = new(false, 0, 0, 0, 0, [], 0, 0);

    public Task<AttachmentInfo> UploadAsync(
        string tenantId, string userId, Guid conversationId, string fileName, string? contentType, Stream content,
        CancellationToken cancellationToken = default) =>
        throw new AttachmentException("attachments_disabled", "File attachments are not available.", 404);

    public Task DeleteAsync(string tenantId, string userId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ResolvedAttachments> ResolveAsync(
        string tenantId, string userId, Guid conversationId, IReadOnlyList<Guid> currentIds, IReadOnlyList<Guid> earlierIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ResolvedAttachments.None);

    public Task MarkRaisedAsync(
        string tenantId, Guid conversationId, IReadOnlyList<Guid> attachmentIds, Guid ticketEventId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
