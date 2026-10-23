using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Kb;

/// <summary>
/// Reads AgentDesk's <c>kb</c> container (partitioned by <c>/tenantId</c>). The field names, the <c>published</c> and
/// <c>public</c> values and the <c>/embedding</c> vector path are AgentDesk's schema (see AgentDesk.Core.Domain.KbArticle).
/// Every query is scoped to one partition, so one tenant can never see another's articles. The account only needs a
/// read role.
/// </summary>
/// <summary>
/// Connects the Cosmos client in the background at startup. Its first call fetches the account topology, which can take
/// ten seconds or more, longer than <see cref="KbOptions.TimeoutSeconds"/>, so without this the first questions after a
/// start would all be answered without the KB. It never delays or fails startup.
/// </summary>
public sealed class CosmosKbWarmup(CosmosClient client, ILogger<CosmosKbWarmup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await client.ReadAccountAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not reach the KB Cosmos account at startup; the first retrievals may be answered without the KB");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CosmosKbSource(Container container, ILogger<CosmosKbSource> logger) : IKbSource
{
    // Values AgentDesk stores; kept as literals so helpdesk does not reference the AgentDesk assemblies.
    private const string DocType = "kbArticle";
    private const string Published = "published";
    private const string Public = "public";
    private const string Ready = "ready";

    private sealed class VectorRow
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("similarity")] public double Similarity { get; set; }
    }

    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static CosmosClient CreateClient(CosmosKbOptions options)
    {
        var clientOptions = new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = SerializerOptions,
            ApplicationName = "helpdesk"
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
        return new CosmosClient(options.Endpoint, new Azure.Identity.DefaultAzureCredential(), clientOptions);
    }

    public async Task<IReadOnlyList<KbDocument>> LoadPublishedAsync(string tenantId, int max, CancellationToken cancellationToken)
    {
        // The vector is not selected, and the body is cut: the lexical index only needs the start of it.
        var query = new QueryDefinition(
                "SELECT c.id, c.number, c.title, c.summary, c.symptoms, c.cause, c.resolution, " +
                "SUBSTRING(c.body, 0, 4000) AS body, c.category, c.tags FROM c " +
                "WHERE c.docType = @docType AND c.status = @status AND c.visibility = @visibility")
            .WithParameter("@docType", DocType)
            .WithParameter("@status", Published)
            .WithParameter("@visibility", Public);

        var documents = new List<KbDocument>();
        try
        {
            using var iterator = container.GetItemQueryIterator<KbDocument>(query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId), MaxItemCount = 100 });
            while (iterator.HasMoreResults && documents.Count < max)
                documents.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // AgentDesk creates the database and container when it first starts, so until then there is no KB. This is
            // not an error for the intake; the warning is there for a wrong Kb:Cosmos:Database or Container name.
            logger.LogWarning("KB container {Database}/{Container} does not exist yet (has AgentDesk started against this account?); treating the KB as empty",
                container.Database.Id, container.Id);
            return [];
        }
        return documents.Take(max).ToList();
    }

    public async Task<IReadOnlyList<KbVectorMatch>> VectorSearchAsync(
        string tenantId, float[] query, string provider, string model, int top, CancellationToken cancellationToken)
    {
        var definition = new QueryDefinition(
                "SELECT TOP @top c.id, VectorDistance(c.embedding, @vector) AS similarity FROM c " +
                "WHERE c.docType = @docType AND c.status = @status AND c.visibility = @visibility " +
                "AND c.embeddingStatus = @ready AND c.embeddingProvider = @provider AND c.embeddingModel = @model " +
                "ORDER BY VectorDistance(c.embedding, @vector)")
            .WithParameter("@top", top)
            .WithParameter("@vector", query)
            .WithParameter("@docType", DocType)
            .WithParameter("@status", Published)
            .WithParameter("@visibility", Public)
            .WithParameter("@ready", Ready)
            .WithParameter("@provider", provider)
            .WithParameter("@model", model);

        var rows = new List<KbVectorMatch>();
        using var iterator = container.GetItemQueryIterator<VectorRow>(definition,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
            rows.AddRange((await iterator.ReadNextAsync(cancellationToken)).Select(r => new KbVectorMatch(r.Id, r.Similarity)));
        return rows;
    }
}
