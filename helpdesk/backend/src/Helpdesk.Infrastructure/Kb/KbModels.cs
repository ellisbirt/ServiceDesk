namespace Helpdesk.Infrastructure.Kb;

public static class KbProviders
{
    public const string Cosmos = "Cosmos";
}

/// <summary>Settings for retrieval over the AgentDesk knowledge base, bound from <c>Kb</c>.</summary>
public sealed class KbOptions
{
    public const string SectionName = "Kb";

    /// <summary>Empty disables retrieval. <see cref="KbProviders.Cosmos"/> reads AgentDesk's kb container directly.</summary>
    public string Provider { get; set; } = "";
    /// <summary>The <c>Llm:Roles</c> entry that embeds the query. It must be the same provider and model AgentDesk embeds articles with.</summary>
    public string EmbeddingRole { get; set; } = "embedding";
    /// <summary>Articles handed to the model per question.</summary>
    public int TopResults { get; set; } = 3;
    /// <summary>Candidates taken from each of the semantic and lexical searches before they are fused.</summary>
    public int CandidatePool { get; set; } = 20;
    /// <summary>Reciprocal rank fusion constant. Larger values flatten the difference between ranks.</summary>
    public int RrfK { get; set; } = 60;
    /// <summary>Cosine similarity a semantic match must reach to count. Tune per embedding model.</summary>
    public double MinSimilarity { get; set; } = 0.35;
    /// <summary>The whole retrieval, including the embedding call, is abandoned after this long and the answer goes ahead without the KB.</summary>
    public int TimeoutSeconds { get; set; } = 5;
    /// <summary>How long a tenant's published articles (for the lexical index) are reused.</summary>
    public int CorpusCacheSeconds { get; set; } = 60;
    /// <summary>Upper bound on articles indexed per tenant.</summary>
    public int MaxArticles { get; set; } = 2000;
    /// <summary>Longest query used, in characters (the newest text wins).</summary>
    public int MaxQueryChars { get; set; } = 1500;
    public int MaxExcerptChars { get; set; } = 1200;
    /// <summary>How many earlier user messages are added to the query so a short follow-up still has its subject.</summary>
    public int HistoryMessages { get; set; } = 2;
    public CosmosKbOptions Cosmos { get; set; } = new();
}

public sealed class CosmosKbOptions
{
    /// <summary>Account endpoint, used with DefaultAzureCredential when no connection string is set. Needs read access only.</summary>
    public string Endpoint { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string Database { get; set; } = "agentdesk";
    public string Container { get; set; } = "kb";
    /// <summary>Accept the emulator's self-signed certificate. Never enable against a real account.</summary>
    public bool AllowInsecureEmulatorCertificate { get; set; }
}

/// <summary>The searchable content of one published, public article.</summary>
public sealed class KbDocument
{
    public string Id { get; set; } = "";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Symptoms { get; set; }
    public string? Cause { get; set; }
    public string? Resolution { get; set; }
    public string? Body { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = [];
}

/// <param name="Similarity">Cosine similarity, 1 is identical.</param>
public sealed record KbVectorMatch(string Id, double Similarity);

/// <summary>Read-only access to the KB store. Only published, public articles are ever returned.</summary>
public interface IKbSource
{
    /// <summary>The tenant's published public articles, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<KbDocument>> LoadPublishedAsync(string tenantId, int max, CancellationToken cancellationToken);

    /// <summary>
    /// Nearest articles to <paramref name="query"/>, best first. Only vectors made by the same provider and model
    /// are compared, because vectors from different models are not comparable.
    /// </summary>
    Task<IReadOnlyList<KbVectorMatch>> VectorSearchAsync(
        string tenantId, float[] query, string provider, string model, int top, CancellationToken cancellationToken);
}
