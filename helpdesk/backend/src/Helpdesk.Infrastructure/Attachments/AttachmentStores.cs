using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Attachments;

/// <summary>An uploaded file's details as kept with the stored file.</summary>
public sealed record StoredAttachment(
    Guid Id, string TenantId, string UserId, Guid ConversationId, string FileName, string ContentType, string Kind, long Size,
    string Sha256, string ScanStatus, DateTimeOffset CreatedUtc, bool NeedsDocument = false, Guid? TicketEventId = null)
{
    public string BlobPath => AttachmentPaths.Content(TenantId, ConversationId, Id);

    public AttachmentInfo ToInfo() => new(Id, FileName, ContentType, Kind, Size, Sha256, BlobPath, ScanStatus, CreatedUtc);
}

/// <summary>
/// Where files live in the shared container: <c>intake/{tenantId}/{conversationId}/{attachmentId}/content</c>. AgentDesk only
/// accepts paths under <c>intake/{tenantId}/</c>, so the layout is part of the contract between the two.
/// </summary>
public static class AttachmentPaths
{
    public const string Root = "intake";

    public static string Prefix(string tenantId, Guid conversationId) => $"{Root}/{tenantId}/{conversationId:D}/";
    public static string Content(string tenantId, Guid conversationId, Guid id) => $"{Prefix(tenantId, conversationId)}{id:D}/content";
    public static string Extracted(string tenantId, Guid conversationId, Guid id) => $"{Prefix(tenantId, conversationId)}{id:D}/extracted.txt";
}

public interface IAttachmentStore
{
    Task PutAsync(StoredAttachment meta, byte[] content, string? extractedText, CancellationToken ct = default);
    Task<StoredAttachment?> GetAsync(string tenantId, Guid conversationId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StoredAttachment>> ListAsync(string tenantId, Guid conversationId, CancellationToken ct = default);
    Task<byte[]> ReadContentAsync(StoredAttachment meta, CancellationToken ct = default);
    Task<string?> ReadExtractedTextAsync(StoredAttachment meta, CancellationToken ct = default);
    Task DeleteAsync(StoredAttachment meta, CancellationToken ct = default);

    /// <summary>Records the ticket the file belongs to, so <see cref="ListExpiredAsync"/> never returns it.</summary>
    Task MarkRaisedAsync(StoredAttachment meta, Guid ticketEventId, CancellationToken ct = default);

    /// <summary>Files created before <paramref name="createdBefore"/> that never became part of a ticket.</summary>
    Task<IReadOnlyList<StoredAttachment>> ListExpiredAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default);
}

/// <summary>
/// A place to check an upload for malware before it is used. <b>No scanner is plugged in yet</b>: the default does not scan,
/// and says so with <see cref="NotScanned"/>, which travels with the file to the ticket. Implement this (for example over
/// Microsoft Defender for Storage or ClamAV) and register it to start scanning; return <see cref="Infected"/> to refuse a file.
/// </summary>
public interface IAttachmentScanner
{
    public const string NotScanned = "not_scanned";
    public const string Clean = "clean";
    public const string Infected = "infected";

    /// <summary>Returns <see cref="NotScanned"/>, <see cref="Clean"/> or <see cref="Infected"/>.</summary>
    Task<string> ScanAsync(StoredAttachment meta, byte[] content, CancellationToken ct = default);
}

/// <summary>The placeholder scanner: scans nothing. See <see cref="IAttachmentScanner"/>.</summary>
public sealed class NoOpAttachmentScanner : IAttachmentScanner
{
    public Task<string> ScanAsync(StoredAttachment meta, byte[] content, CancellationToken ct = default) =>
        Task.FromResult(IAttachmentScanner.NotScanned);
}

public sealed class InMemoryAttachmentStore : IAttachmentStore
{
    private sealed record Entry(StoredAttachment Meta, byte[] Content, string? Text);

    private readonly ConcurrentDictionary<(string, Guid, Guid), Entry> _items = new();

    public Task PutAsync(StoredAttachment meta, byte[] content, string? extractedText, CancellationToken ct = default)
    {
        _items[(meta.TenantId, meta.ConversationId, meta.Id)] = new Entry(meta, content, extractedText);
        return Task.CompletedTask;
    }

    public Task<StoredAttachment?> GetAsync(string tenantId, Guid conversationId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue((tenantId, conversationId, id), out var e) ? e.Meta : null);

    public Task<IReadOnlyList<StoredAttachment>> ListAsync(string tenantId, Guid conversationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredAttachment>>(_items.Values
            .Where(e => e.Meta.TenantId == tenantId && e.Meta.ConversationId == conversationId)
            .Select(e => e.Meta).OrderBy(m => m.CreatedUtc).ToList());

    public Task<byte[]> ReadContentAsync(StoredAttachment meta, CancellationToken ct = default) =>
        Task.FromResult(_items[(meta.TenantId, meta.ConversationId, meta.Id)].Content);

    public Task<string?> ReadExtractedTextAsync(StoredAttachment meta, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue((meta.TenantId, meta.ConversationId, meta.Id), out var e) ? e.Text : null);

    public Task DeleteAsync(StoredAttachment meta, CancellationToken ct = default)
    {
        _items.TryRemove((meta.TenantId, meta.ConversationId, meta.Id), out _);
        return Task.CompletedTask;
    }

    public Task MarkRaisedAsync(StoredAttachment meta, Guid ticketEventId, CancellationToken ct = default)
    {
        var key = (meta.TenantId, meta.ConversationId, meta.Id);
        if (_items.TryGetValue(key, out var e)) _items[key] = e with { Meta = e.Meta with { TicketEventId = ticketEventId } };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredAttachment>> ListExpiredAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredAttachment>>(_items.Values
            .Where(e => e.Meta.TicketEventId is null && e.Meta.CreatedUtc < createdBefore)
            .Select(e => e.Meta).Take(max).ToList());
}

/// <summary>
/// Azure Blob Storage, in the container AgentDesk reads from. File details are kept as blob metadata and the extracted text
/// beside the file, so no database is needed.
/// </summary>
public sealed class AzureAttachmentStore : IAttachmentStore
{
    private readonly BlobContainerClient _container;
    private readonly bool _createContainer;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public AzureAttachmentStore(IOptions<AttachmentOptions> options)
    {
        var o = options.Value;
        BlobServiceClient service;
        if (!string.IsNullOrWhiteSpace(o.ConnectionString)) service = new BlobServiceClient(o.ConnectionString);
        else if (!string.IsNullOrWhiteSpace(o.ServiceUri)) service = new BlobServiceClient(new Uri(o.ServiceUri), new DefaultAzureCredential());
        else throw new InvalidOperationException("Attachments:ConnectionString or Attachments:ServiceUri must be set when Attachments:Provider is Azure.");
        _container = service.GetBlobContainerClient(o.Container);
        _createContainer = o.CreateContainerIfNotExists;
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_initialized || !_createContainer) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await _container.CreateIfNotExistsAsync(cancellationToken: ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task PutAsync(StoredAttachment meta, byte[] content, string? extractedText, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await _container.GetBlobClient(meta.BlobPath).UploadAsync(
            BinaryData.FromBytes(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = meta.ContentType }, Metadata = ToMetadata(meta) }, ct);
        if (extractedText is not null)
        {
            await _container.GetBlobClient(AttachmentPaths.Extracted(meta.TenantId, meta.ConversationId, meta.Id))
                .UploadAsync(BinaryData.FromString(extractedText), overwrite: true, ct);
        }
    }

    public async Task<StoredAttachment?> GetAsync(string tenantId, Guid conversationId, Guid id, CancellationToken ct = default)
    {
        try
        {
            var props = await _container.GetBlobClient(AttachmentPaths.Content(tenantId, conversationId, id)).GetPropertiesAsync(cancellationToken: ct);
            return FromMetadata(props.Value.Metadata, props.Value.CreatedOn);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<StoredAttachment>> ListAsync(string tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var result = new List<StoredAttachment>();
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix: AttachmentPaths.Prefix(tenantId, conversationId), cancellationToken: ct))
        {
            if (!blob.Name.EndsWith("/content", StringComparison.Ordinal)) continue;
            if (FromMetadata(blob.Metadata, blob.Properties.CreatedOn) is { } meta) result.Add(meta);
        }
        return result.OrderBy(m => m.CreatedUtc).ToList();
    }

    public async Task<byte[]> ReadContentAsync(StoredAttachment meta, CancellationToken ct = default) =>
        (await _container.GetBlobClient(meta.BlobPath).DownloadContentAsync(ct)).Value.Content.ToArray();

    public async Task<string?> ReadExtractedTextAsync(StoredAttachment meta, CancellationToken ct = default)
    {
        try
        {
            return (await _container.GetBlobClient(AttachmentPaths.Extracted(meta.TenantId, meta.ConversationId, meta.Id))
                .DownloadContentAsync(ct)).Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(StoredAttachment meta, CancellationToken ct = default)
    {
        await _container.GetBlobClient(meta.BlobPath).DeleteIfExistsAsync(cancellationToken: ct);
        await _container.GetBlobClient(AttachmentPaths.Extracted(meta.TenantId, meta.ConversationId, meta.Id)).DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task MarkRaisedAsync(StoredAttachment meta, Guid ticketEventId, CancellationToken ct = default) =>
        await _container.GetBlobClient(meta.BlobPath).SetMetadataAsync(ToMetadata(meta with { TicketEventId = ticketEventId }), cancellationToken: ct);

    public async Task<IReadOnlyList<StoredAttachment>> ListExpiredAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default)
    {
        var result = new List<StoredAttachment>();
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix: AttachmentPaths.Root + "/", cancellationToken: ct))
        {
            if (!blob.Name.EndsWith("/content", StringComparison.Ordinal)) continue;
            if (FromMetadata(blob.Metadata, blob.Properties.CreatedOn) is { TicketEventId: null } meta && meta.CreatedUtc < createdBefore)
            {
                result.Add(meta);
                if (result.Count >= max) break;
            }
        }
        return result;
    }

    // Blob metadata values must be ASCII, so the file name is encoded.
    private static Dictionary<string, string> ToMetadata(StoredAttachment m)
    {
        var data = new Dictionary<string, string>
        {
            ["id"] = m.Id.ToString("D"),
            ["tenantid"] = m.TenantId,
            ["userid"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(m.UserId)),
            ["conversationid"] = m.ConversationId.ToString("D"),
            ["filename"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(m.FileName)),
            ["contenttype"] = m.ContentType,
            ["kind"] = m.Kind,
            ["size"] = m.Size.ToString(CultureInfo.InvariantCulture),
            ["sha256"] = m.Sha256,
            ["scan"] = m.ScanStatus,
            ["created"] = m.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["needsdoc"] = m.NeedsDocument ? "1" : "0"
        };
        if (m.TicketEventId is { } ticket) data["ticket"] = ticket.ToString("D");
        return data;
    }

    private static StoredAttachment? FromMetadata(IDictionary<string, string> d, DateTimeOffset? fallbackCreated)
    {
        try
        {
            string Text(string key) => Encoding.UTF8.GetString(Convert.FromBase64String(d[key]));
            return new StoredAttachment(
                Guid.Parse(d["id"]), d["tenantid"], Text("userid"), Guid.Parse(d["conversationid"]), Text("filename"), d["contenttype"],
                d["kind"], long.Parse(d["size"], CultureInfo.InvariantCulture), d["sha256"], d["scan"],
                DateTimeOffset.TryParse(d["created"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created)
                    ? created : fallbackCreated ?? DateTimeOffset.UtcNow,
                d.TryGetValue("needsdoc", out var needs) && needs == "1",
                d.TryGetValue("ticket", out var ticket) && Guid.TryParse(ticket, out var ticketId) ? ticketId : null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}
