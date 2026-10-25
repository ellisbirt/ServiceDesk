using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Core.Interfaces;
using Helpdesk.Dlp;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Llm;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Attachments;

/// <summary>
/// Accepts, checks, stores and prepares chat attachments. Uploads are refused unless the content matches a supported type
/// and the limits in <see cref="AttachmentOptions"/> allow them. File text reaches the model reduced to a token budget,
/// redacted for the model, and wrapped as untrusted data.
/// </summary>
public sealed class AttachmentService(
    IAttachmentStore store,
    IAttachmentScanner scanner,
    ISensitiveDataRedactor redactor,
    IOptions<AttachmentOptions> options,
    TimeProvider clock,
    ILogger<AttachmentService> logger) : IAttachmentService
{
    private AttachmentOptions Options => options.Value;

    public bool Enabled => true;

    private IReadOnlyList<string> Allowed => Options.EffectiveExtensions();

    public AttachmentLimits Limits => new(
        true, Options.MaxFileBytes, Options.MaxFilesPerMessage, Options.MaxFilesPerConversation, Options.MaxTotalBytesPerConversation,
        Allowed, Options.PasteMinChars, Options.PasteMinLines);

    public async Task<AttachmentInfo> UploadAsync(
        string tenantId, string userId, Guid conversationId, string fileName, string? contentType, Stream content,
        CancellationToken cancellationToken = default)
    {
        CheckSegment(tenantId);
        CheckSegment(userId);
        var o = Options;

        var bytes = await ReadLimitedAsync(content, o.MaxFileBytes, cancellationToken);
        if (bytes.Length == 0) throw new AttachmentException("unreadable_file", "The file is empty.", 422);

        var existing = await store.ListAsync(tenantId, conversationId, cancellationToken);
        if (existing.Count >= o.MaxFilesPerConversation)
            throw new AttachmentException("too_many_files", $"A conversation can have at most {o.MaxFilesPerConversation} files.");
        if (existing.Sum(e => e.Size) + bytes.Length > o.MaxTotalBytesPerConversation)
            throw new AttachmentException("conversation_quota_exceeded", "This conversation has reached its limit for attached files.");

        var name = CleanFileName(fileName);
        var sniffed = AttachmentTypes.Sniff(name, bytes, Allowed, o);
        var extracted = sniffed.Kind is AttachmentKinds.Image
            ? null
            : AttachmentExtractor.Extract(sniffed, bytes, o);

        var meta = new StoredAttachment(
            Guid.NewGuid(), tenantId, userId, conversationId, name, sniffed.ContentType, sniffed.Kind, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), IAttachmentScanner.NotScanned, clock.GetUtcNow(),
            NeedsDocument: sniffed.Kind == AttachmentKinds.Pdf && extracted?.NoText == true);

        var scan = await scanner.ScanAsync(meta, bytes, cancellationToken);
        if (scan == IAttachmentScanner.Infected)
            throw new AttachmentException("unreadable_file", "The file was blocked by the virus scan.", 422);
        meta = meta with { ScanStatus = scan };

        await store.PutAsync(meta, bytes, extracted?.Text, cancellationToken);
        logger.LogInformation(
            "Stored a {Kind} attachment of {Size} bytes for Tenant: {TenantId} (scan: {Scan})", meta.Kind, meta.Size, tenantId, scan);
        return meta.ToInfo();
    }

    public async Task DeleteAsync(string tenantId, string userId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var meta = await store.GetAsync(tenantId, conversationId, attachmentId, cancellationToken);
        // A file on a raised ticket is part of the ticket and is not removed from the chat.
        if (meta is null || meta.UserId != userId || meta.TicketEventId is not null) return;
        await store.DeleteAsync(meta, cancellationToken);
    }

    public async Task<ResolvedAttachments> ResolveAsync(
        string tenantId, string userId, Guid conversationId, IReadOnlyList<Guid> currentIds, IReadOnlyList<Guid> earlierIds,
        CancellationToken cancellationToken = default)
    {
        if (currentIds.Count == 0 && earlierIds.Count == 0) return ResolvedAttachments.None;

        var current = await LoadAsync(tenantId, userId, conversationId, currentIds, cancellationToken);
        var seen = current.Select(c => c.Id).ToHashSet();
        var earlier = await LoadAsync(tenantId, userId, conversationId, earlierIds.Where(id => !seen.Contains(id)).ToList(), cancellationToken);

        var context = await BuildContextAsync(tenantId, current, earlier, cancellationToken);
        return new ResolvedAttachments(
            context, [.. current.Select(c => c.ToInfo())], [.. current.Concat(earlier).Select(c => c.ToInfo())]);
    }

    public async Task MarkRaisedAsync(
        string tenantId, Guid conversationId, IReadOnlyList<Guid> attachmentIds, Guid ticketEventId, CancellationToken cancellationToken = default)
    {
        foreach (var id in attachmentIds.Distinct())
        {
            if (await store.GetAsync(tenantId, conversationId, id, cancellationToken) is { } meta)
                await store.MarkRaisedAsync(meta, ticketEventId, cancellationToken);
        }
    }

    // ----- What the model is shown -----

    private async Task<List<StoredAttachment>> LoadAsync(
        string tenantId, string userId, Guid conversationId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var result = new List<StoredAttachment>();
        foreach (var id in ids.Distinct())
        {
            var meta = await store.GetAsync(tenantId, conversationId, id, ct);
            if (meta is null || meta.UserId != userId)
            {
                logger.LogWarning("An attachment referenced by a message was not found for Tenant: {TenantId}", tenantId);
                continue;
            }
            result.Add(meta);
        }
        return result;
    }

    private async Task<AttachmentContext> BuildContextAsync(
        string tenantId, List<StoredAttachment> current, List<StoredAttachment> earlier, CancellationToken ct)
    {
        var o = Options;
        var text = new StringBuilder();
        var parts = new List<ChatContentPart>();
        var tokens = 0;
        var budget = o.MaxTokensPerRequest;

        foreach (var (meta, isCurrent) in current.Select(c => (c, true)).Concat(earlier.Select(e => (e, false))))
        {
            var header = $"File: {meta.FileName}\nType: {meta.Kind} ({meta.ContentType}), {meta.Size.ToString(CultureInfo.InvariantCulture)} bytes";
            string body;

            if (meta.Kind == AttachmentKinds.Image)
            {
                body = isCurrent ? "(an image; it is shown to you if you can see images)" : "(an image attached earlier; not shown again)";
                if (isCurrent)
                {
                    parts.Add(new ChatContentPart(ChatContentKinds.Image, meta.ContentType, await store.ReadContentAsync(meta, ct), meta.FileName));
                    tokens += o.ImageTokenEstimate;
                }
            }
            else if (meta.NeedsDocument)
            {
                body = isCurrent
                    ? "(a scanned PDF with no text layer; it is shown to you if you can read PDFs)"
                    : "(a scanned PDF attached earlier; not shown again)";
                if (isCurrent)
                {
                    parts.Add(new ChatContentPart(ChatContentKinds.Document, meta.ContentType, await store.ReadContentAsync(meta, ct), meta.FileName));
                    tokens += o.ImageTokenEstimate * 2;
                }
            }
            else
            {
                var allowed = Math.Min(isCurrent ? o.MaxExtractedTokensPerFile : o.MaxEarlierTokensPerFile, Math.Max(0, budget));
                if (allowed < 100)
                {
                    body = "(not shown: the limit for attached text in one request was reached)";
                }
                else
                {
                    var raw = await store.ReadExtractedTextAsync(meta, ct) ?? "";
                    var (reduced, wasReduced) = ContentReducer.Reduce(raw, allowed);
                    // Redacting after reducing keeps the scan small; secrets in the lines that were dropped never leave the service.
                    body = redactor.Redact(reduced, EgressDestination.Model, tenantId).Text;
                    if (wasReduced) header += "\nThe file is long, so only the start, the end and the lines that look like errors are shown.";
                    var used = ContentReducer.Tokens(body);
                    tokens += used;
                    budget -= used;
                }
            }

            text.Append(PromptInput.Wrap("attachment", $"{header}\n\n{body}")).Append("\n\n");
        }

        return new AttachmentContext(
            text.ToString(), parts, tokens, [.. current.Concat(earlier).Select(c => c.ToInfo())]);
    }

    // ----- Helpers -----

    private static async Task<byte[]> ReadLimitedAsync(Stream content, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
                throw new AttachmentException("file_too_large", $"Files can be at most {max} bytes.", 413);
        }
        return buffer.ToArray();
    }

    /// <summary>Tenant and user ids end up in storage paths, so anything that could change the path is refused.</summary>
    private static void CheckSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains('/') || value.Contains('\\')
            || value.Contains("..", StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new AttachmentException("forbidden", "Attachments are not available for this account.", 403);
    }

    internal static string CleanFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/').Split('/')[^1]);
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim().TrimStart('.');
        if (name.Length > 120) name = name[^120..];
        return name.Length == 0 ? "file" : name;
    }
}
