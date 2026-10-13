using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using AgentDesk.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentDesk.Core.Services;

public sealed class AttachmentOptions
{
    public const string Section = "Attachments";

    public long MaxBytes { get; set; } = 25 * 1024 * 1024;
    public int MaxPerTicket { get; set; } = 50;
    public int SasLifetimeMinutes { get; set; } = 10;

    public List<string> AllowedContentTypes { get; set; } =
    [
        "image/png", "image/jpeg", "image/gif", "image/webp",
        "application/pdf", "text/plain", "text/csv", "application/json",
        "application/zip", "application/x-zip-compressed",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "message/rfc822", "application/vnd.ms-outlook", "application/octet-stream"
    ];

    public List<string> AllowedExtensions { get; set; } =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".txt", ".log", ".csv", ".json",
        ".zip", ".docx", ".xlsx", ".pptx", ".eml", ".msg", ".har"
    ];
}

public sealed class AcceptAllAttachmentScanner : IAttachmentScanner
{
    public Task<(string Status, string? Result)> ScanAsync(Attachment attachment, CancellationToken ct = default) =>
        Task.FromResult<(string, string?)>((AttachmentStatuses.Available, "not_scanned"));
}

/// <summary>
/// Two-step direct-to-blob upload: the API issues a short-lived write URL, the browser PUTs the file to Blob Storage, then
/// the client calls complete so the API can verify size and hash, run the scanner and make the file available.
/// </summary>
public sealed class AttachmentService(
    IAttachmentRepository repository,
    ITicketRepository tickets,
    TicketService ticketService,
    IBlobStore blobs,
    IAttachmentScanner scanner,
    IClock clock,
    IOptions<AttachmentOptions> options,
    ILogger<AttachmentService> logger)
{
    private AttachmentOptions Options => options.Value;
    private TimeSpan SasLifetime => TimeSpan.FromMinutes(Math.Clamp(Options.SasLifetimeMinutes, 1, 60));

    public async Task<AttachmentUpload> CreateUploadAsync(CurrentUser user, string ticketId, CreateAttachmentCommand command, CancellationToken ct = default)
    {
        var fileName = SanitizeFileName(command.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = (command.ContentType ?? "").Trim().ToLowerInvariant();
        new Validator()
            .Required("fileName", fileName, 255)
            .When(!Options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase), "fileName",
                $"extension '{extension}' is not allowed.")
            .When(!Options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase), "contentType",
                $"'{contentType}' is not allowed.")
            .When(command.Size <= 0 || command.Size > Options.MaxBytes, "size", $"must be between 1 and {Options.MaxBytes} bytes.")
            .When(command.Sha256 is { } h && !IsSha256(h), "sha256", "must be a 64-character hex SHA-256.")
            .ThrowIfInvalid();

        var ticket = await tickets.GetAsync(user.TenantId, ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        if (TicketStateMachine.IsTerminal(ticket.Status))
            throw new InvalidTransitionException($"Files cannot be attached to a {ticket.Status} ticket.");
        var existing = await repository.ListForTicketAsync(user.TenantId, ticketId, ct);
        if (existing.Count(a => a.Status != AttachmentStatuses.Rejected) >= Options.MaxPerTicket)
            throw ValidationException.For("ticket", $"A ticket can have at most {Options.MaxPerTicket} attachments.");

        var now = clock.UtcNow;
        var id = TenantDocument.NewId();
        var attachment = await repository.CreateAsync(new Attachment
        {
            Id = id,
            TenantId = user.TenantId,
            TicketId = ticketId,
            FileName = fileName,
            ContentType = contentType,
            Size = command.Size,
            Sha256 = command.Sha256?.ToLowerInvariant(),
            BlobPath = Attachment.PathFor(user.TenantId, ticketId, id),
            Status = AttachmentStatuses.PendingUpload,
            UploadedBy = user.AsPerson(),
            CreatedAt = now,
            CreatedBy = user.UserId,
            UpdatedAt = now,
            UpdatedBy = user.UserId
        }, ct);

        var uri = await blobs.GetUploadUriAsync(attachment.BlobPath, contentType, SasLifetime, ct);
        return new AttachmentUpload(attachment, uri, now + SasLifetime);
    }

    public async Task<Attachment> CompleteAsync(CurrentUser user, string ticketId, string attachmentId, CancellationToken ct = default)
    {
        var attachment = await GetForTicketAsync(user, ticketId, attachmentId, ct);
        if (attachment.Status != AttachmentStatuses.PendingUpload) return attachment;

        var properties = await blobs.GetPropertiesAsync(attachment.BlobPath, computeSha256: attachment.Sha256 is not null, ct)
            ?? throw ValidationException.For("attachment", "The file has not been uploaded yet.");

        string? problem = null;
        if (properties.Size != attachment.Size) problem = $"Uploaded size {properties.Size} does not match declared size {attachment.Size}.";
        else if (properties.Size > Options.MaxBytes) problem = "The uploaded file is too large.";
        else if (attachment.Sha256 is not null && !string.Equals(properties.Sha256Hex, attachment.Sha256, StringComparison.OrdinalIgnoreCase))
            problem = "The uploaded file's SHA-256 does not match the declared hash.";

        if (problem is not null)
        {
            attachment.Status = AttachmentStatuses.Rejected;
            attachment.ScanResult = problem;
            Touch(attachment, user);
            await repository.ReplaceAsync(attachment, attachment.ETag, ct);
            await TryDeleteBlobAsync(attachment);
            throw ValidationException.For("attachment", problem);
        }

        var (status, result) = await scanner.ScanAsync(attachment, ct);
        attachment.Status = status;
        attachment.ScanResult = result;
        Touch(attachment, user);
        var saved = await repository.ReplaceAsync(attachment, attachment.ETag, ct);
        if (saved.Status == AttachmentStatuses.Available)
            await ticketService.RecordAttachmentAsync(user, saved, ct);
        else
            logger.LogWarning("Attachment {AttachmentId} on ticket {TicketId} was {Status} by the scanner", saved.Id, ticketId, saved.Status);
        return saved;
    }

    public async Task<IReadOnlyList<Attachment>> ListAsync(CurrentUser user, string ticketId, CancellationToken ct = default)
    {
        _ = await tickets.GetAsync(user.TenantId, ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        var all = await repository.ListForTicketAsync(user.TenantId, ticketId, ct);
        return all.Where(a => a.Status == AttachmentStatuses.Available).OrderBy(a => a.CreatedAt).ToList();
    }

    public async Task<AttachmentDownload> GetDownloadAsync(CurrentUser user, string ticketId, string attachmentId, CancellationToken ct = default)
    {
        var attachment = await GetForTicketAsync(user, ticketId, attachmentId, ct);
        if (attachment.Status != AttachmentStatuses.Available) throw new NotFoundException("Attachment", attachmentId);
        var uri = await blobs.GetDownloadUriAsync(attachment.BlobPath, attachment.FileName, attachment.ContentType, SasLifetime, ct);
        return new AttachmentDownload(uri, clock.UtcNow + SasLifetime);
    }

    private async Task<Attachment> GetForTicketAsync(CurrentUser user, string ticketId, string attachmentId, CancellationToken ct)
    {
        var attachment = await repository.GetAsync(user.TenantId, attachmentId, ct);
        if (attachment is null || attachment.TicketId != ticketId) throw new NotFoundException("Attachment", attachmentId);
        return attachment;
    }

    private void Touch(Attachment attachment, CurrentUser user)
    {
        attachment.UpdatedAt = clock.UtcNow;
        attachment.UpdatedBy = user.UserId;
    }

    private async Task TryDeleteBlobAsync(Attachment attachment)
    {
        try { await blobs.DeleteAsync(attachment.BlobPath); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not delete rejected blob for attachment {AttachmentId}", attachment.Id); }
    }

    internal static string SanitizeFileName(string? name)
    {
        var raw = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last()).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(raw.Select(c => char.IsControl(c) || invalid.Contains(c) || c is '"' or '<' or '>' ? '_' : c).ToArray());
        return clean.Trim('.', ' ');
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
