using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentDesk.Infrastructure.Cosmos;

public sealed class CosmosOptions
{
    public const string Section = "Cosmos";

    /// <summary>Account endpoint; used with managed identity / DefaultAzureCredential when no connection string is set.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Key-based connection string (local emulator). Prefer <see cref="Endpoint"/> with Entra ID in Azure.</summary>
    public string? ConnectionString { get; set; }
    public string Database { get; set; } = "agentdesk";
    public string TicketsContainer { get; set; } = "tickets";
    public string KbContainer { get; set; } = "kb";
    public string AttachmentsContainer { get; set; } = "attachments";
    /// <summary>Create the database and containers on startup (local/dev). In Azure these are provisioned by Bicep.</summary>
    public bool CreateIfNotExists { get; set; } = true;
    /// <summary>Add a vector embedding policy and index on <c>/embedding</c> in the kb container (needs vector search enabled on the account).</summary>
    public bool EnableVectorPolicy { get; set; } = true;
    public int EmbeddingDimensions { get; set; } = 1536;
    /// <summary>Accept the emulator's self-signed certificate. Never enable against a real account.</summary>
    public bool AllowInsecureEmulatorCertificate { get; set; }
}

public sealed class CosmosContainers(Container tickets, Container kb, Container attachments)
{
    public Container Tickets { get; } = tickets;
    public Container Kb { get; } = kb;
    public Container Attachments { get; } = attachments;

    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static CosmosClient CreateClient(CosmosOptions options)
    {
        var clientOptions = new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = SerializerOptions,
            ApplicationName = "agentdesk",
            EnableContentResponseOnWrite = true
        };
        if (options.AllowInsecureEmulatorCertificate)
        {
            clientOptions.ConnectionMode = ConnectionMode.Gateway;
            clientOptions.HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            return new CosmosClient(options.ConnectionString, clientOptions);
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            return new CosmosClient(options.Endpoint, new DefaultAzureCredential(), clientOptions);
        throw new InvalidOperationException("Cosmos:ConnectionString or Cosmos:Endpoint must be set when Storage:Provider is Cosmos.");
    }
}

/// <summary>Creates the database and containers on startup when <see cref="CosmosOptions.CreateIfNotExists"/> is set.</summary>
public sealed class CosmosInitializer(CosmosClient client, IOptions<CosmosOptions> options, ILogger<CosmosInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.CreateIfNotExists) return;

        var db = (await client.CreateDatabaseIfNotExistsAsync(o.Database, cancellationToken: ct)).Database;
        await db.CreateContainerIfNotExistsAsync(
            new ContainerProperties(o.TicketsContainer, "/tenantId") { DefaultTimeToLive = -1 }, cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(o.AttachmentsContainer, "/tenantId"), cancellationToken: ct);

        var kb = new ContainerProperties(o.KbContainer, "/tenantId");
        // The emulator rejects an excluded path unless "/*" is also included explicitly.
        kb.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        kb.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/embedding/*" });
        if (o.EnableVectorPolicy)
        {
            kb.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>
            {
                new()
                {
                    Path = "/embedding",
                    DataType = VectorDataType.Float32,
                    DistanceFunction = DistanceFunction.Cosine,
                    Dimensions = o.EmbeddingDimensions
                }
            });
            kb.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath { Path = "/embedding", Type = VectorIndexType.QuantizedFlat });
        }

        try
        {
            await db.CreateContainerIfNotExistsAsync(kb, cancellationToken: ct);
        }
        catch (CosmosException ex) when (o.EnableVectorPolicy && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            logger.LogWarning(ex,
                "Could not create the kb container with a vector policy (is vector search enabled on the account?). Creating it without one; embeddings are still stored");
            kb.VectorEmbeddingPolicy = null;
            kb.IndexingPolicy.VectorIndexes.Clear();
            await db.CreateContainerIfNotExistsAsync(kb, cancellationToken: ct);
        }
        logger.LogInformation("Cosmos DB database {Database} is ready", o.Database);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

internal static class CosmosErrors
{
    public static async Task<T> Map<T>(Func<Task<T>> action, string resource, string id)
    {
        try
        {
            return await action();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DuplicateException($"A {resource} with id '{id}' already exists.");
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ConcurrencyConflictException();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException(resource, id);
        }
    }

    public static async Task<T?> ReadOrNull<T>(Container container, string tenantId, string id, CancellationToken ct)
    {
        try
        {
            return (await container.ReadItemAsync<T>(id, new PartitionKey(tenantId), cancellationToken: ct)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
    }

    public static ItemRequestOptions? IfMatch(string? etag) => etag is null ? null : new ItemRequestOptions { IfMatchEtag = etag };

    public static async Task<Page<T>> ReadPage<T>(Container container, QueryDefinition query, string? tenantId, int pageSize,
        string? token, CancellationToken ct)
    {
        var options = new QueryRequestOptions { MaxItemCount = pageSize };
        if (tenantId is not null) options.PartitionKey = new PartitionKey(tenantId);
        using var it = container.GetItemQueryIterator<T>(query, token, options);
        if (!it.HasMoreResults) return new Page<T>([], null);
        var response = await it.ReadNextAsync(ct);
        return new Page<T>(response.ToList(), response.ContinuationToken);
    }

    public static async Task<List<T>> ReadAll<T>(Container container, QueryDefinition query, string? tenantId, CancellationToken ct,
        int? limit = null)
    {
        var options = new QueryRequestOptions();
        if (tenantId is not null) options.PartitionKey = new PartitionKey(tenantId);
        var results = new List<T>();
        using var it = container.GetItemQueryIterator<T>(query, requestOptions: options);
        while (it.HasMoreResults && (limit is null || results.Count < limit))
            results.AddRange(await it.ReadNextAsync(ct));
        return limit is { } l ? results.Take(l).ToList() : results;
    }
}
