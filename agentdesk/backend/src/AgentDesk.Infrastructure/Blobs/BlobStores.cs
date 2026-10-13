using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Abstractions;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using BlobProperties = AgentDesk.Core.Abstractions.BlobProperties;

namespace AgentDesk.Infrastructure.Blobs;

public sealed class BlobStorageOptions
{
    public const string Section = "Blobs";

    /// <summary>Key-based connection string (Azurite: <c>UseDevelopmentStorage=true</c>). Account-key SAS is used.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>Blob service URI for Entra ID auth (managed identity). User delegation SAS is used.</summary>
    public string? ServiceUri { get; set; }
    public string Container { get; set; } = "ticket-attachments";
    public bool CreateContainerIfNotExists { get; set; } = true;
    /// <summary>
    /// Public base URL of the API, used by the in-memory store to build upload/download URLs. Defaults to the
    /// API's local address.
    /// </summary>
    public string DevPublicBaseUrl { get; set; } = "http://localhost:5139";
}

/// <summary>Azure Blob Storage implementation. Clients upload and download directly with short-lived, single-blob SAS URLs.</summary>
public sealed class AzureBlobStore : IBlobStore
{
    private readonly BlobServiceClient service;
    private readonly BlobContainerClient container;
    private readonly bool useDelegationKey;
    private readonly SemaphoreSlim initLock = new(1, 1);
    private readonly bool createContainer;
    private volatile bool initialized;
    private UserDelegationKey? delegationKey;

    public AzureBlobStore(IOptions<BlobStorageOptions> options)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
        {
            service = new BlobServiceClient(o.ConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(o.ServiceUri))
        {
            service = new BlobServiceClient(new Uri(o.ServiceUri), new DefaultAzureCredential());
            useDelegationKey = true;
        }
        else
        {
            throw new InvalidOperationException("Blobs:ConnectionString or Blobs:ServiceUri must be set when Blobs:Provider is Azure.");
        }
        container = service.GetBlobContainerClient(o.Container);
        createContainer = o.CreateContainerIfNotExists;
    }

    public async Task<Uri> GetUploadUriAsync(string path, string contentType, TimeSpan lifetime, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        var builder = NewSas(path, lifetime, BlobSasPermissions.Create | BlobSasPermissions.Write);
        builder.ContentType = contentType;
        return await SignAsync(path, builder, ct);
    }

    public async Task<Uri> GetDownloadUriAsync(string path, string fileName, string contentType, TimeSpan lifetime, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        var builder = NewSas(path, lifetime, BlobSasPermissions.Read);
        builder.ContentDisposition = ContentDisposition(fileName);
        builder.ContentType = contentType;
        return await SignAsync(path, builder, ct);
    }

    public async Task<BlobProperties?> GetPropertiesAsync(string path, bool computeSha256, CancellationToken ct = default)
    {
        var blob = container.GetBlobClient(path);
        try
        {
            var props = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            string? sha = null;
            if (computeSha256)
            {
                await using var stream = await blob.OpenReadAsync(cancellationToken: ct);
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            }
            return new BlobProperties(props.ContentLength, props.ContentType, sha);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default) =>
        await container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);

    private BlobSasBuilder NewSas(string path, TimeSpan lifetime, BlobSasPermissions permissions)
    {
        var builder = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = path,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-2),
            ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime),
            Protocol = useDelegationKey ? SasProtocol.Https : SasProtocol.HttpsAndHttp
        };
        builder.SetPermissions(permissions);
        return builder;
    }

    private async Task<Uri> SignAsync(string path, BlobSasBuilder builder, CancellationToken ct)
    {
        var blob = container.GetBlobClient(path);
        if (!useDelegationKey) return blob.GenerateSasUri(builder);

        var key = delegationKey;
        if (key is null || key.SignedExpiresOn < DateTimeOffset.UtcNow.AddMinutes(70))
        {
            key = (await service.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(6), ct)).Value;
            delegationKey = key;
        }
        var sas = builder.ToSasQueryParameters(key, service.AccountName).ToString();
        return new UriBuilder(blob.Uri) { Query = sas }.Uri;
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (initialized || !createContainer) return;
        await initLock.WaitAsync(ct);
        try
        {
            if (!initialized)
            {
                await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
                initialized = true;
            }
        }
        finally
        {
            initLock.Release();
        }
    }

    internal static string ContentDisposition(string fileName)
    {
        var ascii = new string(fileName.Select(ch => ch is >= ' ' and < (char)127 and not '"' and not '\\' ? ch : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}

/// <summary>
/// Development and test store. Blobs live in memory and are reached through the API's <c>/dev-blobs/{path}</c>
/// endpoints, guarded by an HMAC signature and expiry in the query string (the same shape as a SAS URL).
/// </summary>
public sealed class InMemoryBlobStore(IOptions<BlobStorageOptions> options, IClock clock) : IBlobStore
{
    public const string RoutePrefix = "/dev-blobs/";

    private readonly ConcurrentDictionary<string, StoredBlob> blobs = new(StringComparer.Ordinal);
    private readonly byte[] signingKey = RandomNumberGenerator.GetBytes(32);

    public sealed record StoredBlob(byte[] Content, string ContentType);

    public Task<Uri> GetUploadUriAsync(string path, string contentType, TimeSpan lifetime, CancellationToken ct = default) =>
        Task.FromResult(BuildUri(path, "w", lifetime, null));

    public Task<Uri> GetDownloadUriAsync(string path, string fileName, string contentType, TimeSpan lifetime, CancellationToken ct = default) =>
        Task.FromResult(BuildUri(path, "r", lifetime, fileName));

    public Task<BlobProperties?> GetPropertiesAsync(string path, bool computeSha256, CancellationToken ct = default) =>
        Task.FromResult(blobs.TryGetValue(path, out var b)
            ? new BlobProperties(b.Content.LongLength, b.ContentType, computeSha256 ? Convert.ToHexStringLower(SHA256.HashData(b.Content)) : null)
            : null);

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        blobs.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    public void Put(string path, byte[] content, string contentType) => blobs[path] = new StoredBlob(content, contentType);

    public StoredBlob? Get(string path) => blobs.TryGetValue(path, out var b) ? b : null;

    /// <summary>Checks the signature and expiry for <paramref name="permission"/> ("r" or "w").</summary>
    public bool Verify(string path, string permission, string? expires, string? fileName, string? signature)
    {
        if (!long.TryParse(expires, out var exp) || clock.UtcNow.ToUnixTimeSeconds() > exp || string.IsNullOrEmpty(signature))
            return false;
        var expected = Sign(path, permission, exp, fileName);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    private Uri BuildUri(string path, string permission, TimeSpan lifetime, string? fileName)
    {
        var exp = clock.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var query = $"sp={permission}&se={exp}&sig={Uri.EscapeDataString(Sign(path, permission, exp, fileName))}";
        if (fileName is not null) query += $"&fn={Uri.EscapeDataString(fileName)}";
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"{options.Value.DevPublicBaseUrl.TrimEnd('/')}{RoutePrefix}{encodedPath}?{query}");
    }

    private string Sign(string path, string permission, long expires, string? fileName)
    {
        var payload = Encoding.UTF8.GetBytes($"{permission}\n{expires}\n{path}\n{fileName}");
        return Convert.ToBase64String(HMACSHA256.HashData(signingKey, payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
