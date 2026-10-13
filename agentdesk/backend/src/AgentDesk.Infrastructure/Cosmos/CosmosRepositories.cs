using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using Microsoft.Azure.Cosmos;

namespace AgentDesk.Infrastructure.Cosmos;

public sealed class CosmosTicketRepository(CosmosContainers containers, IClock clock) : ITicketRepository
{
    private Container C => containers.Tickets;

    public async Task<Ticket?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await CosmosErrors.ReadOrNull<Ticket>(C, tenantId, id, ct) is { DocType: DocTypes.Ticket } t ? t : null;

    public Task<Ticket> CreateAsync(Ticket ticket, CancellationToken ct = default) =>
        CosmosErrors.Map(async () => (await C.CreateItemAsync(ticket, new PartitionKey(ticket.TenantId), cancellationToken: ct)).Resource,
            "ticket", ticket.Id);

    public Task<Ticket> ReplaceAsync(Ticket ticket, string? ifMatch, CancellationToken ct = default) =>
        CosmosErrors.Map(async () => (await C.ReplaceItemAsync(ticket, ticket.Id, new PartitionKey(ticket.TenantId),
            CosmosErrors.IfMatch(ifMatch), ct)).Resource, "ticket", ticket.Id);

    public Task<Page<Ticket>> QueryAsync(string tenantId, TicketQuery q, CancellationToken ct = default)
    {
        var where = new List<string> { "c.docType = @docType" };
        var parameters = new Dictionary<string, object> { ["@docType"] = DocTypes.Ticket };
        if (q.Statuses is { Count: > 0 })
        {
            where.Add("ARRAY_CONTAINS(@statuses, c.status)");
            parameters["@statuses"] = q.Statuses;
        }
        if (q.Priority is { } p) { where.Add("c.priority = @priority"); parameters["@priority"] = p; }
        if (q.Unassigned) where.Add("(NOT IS_DEFINED(c.assignee) OR IS_NULL(c.assignee))");
        else if (q.AssigneeId is { } a) { where.Add("c.assignee.id = @assignee"); parameters["@assignee"] = a; }
        if (q.AssignmentGroup is { } g) { where.Add("c.assignmentGroup = @group"); parameters["@group"] = g; }
        if (q.Category is { } cat) { where.Add("c.category = @category"); parameters["@category"] = cat; }
        if (q.RequesterId is { } r) { where.Add("c.requester.id = @requester"); parameters["@requester"] = r; }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("(CONTAINS(c.number, @text, true) OR CONTAINS(c.subject, @text, true))");
            parameters["@text"] = q.Text.Trim();
        }
        if (q.Breached is { } breached)
        {
            const string isBreached = "(ARRAY_CONTAINS(@active, c.status) AND (((NOT IS_DEFINED(c.sla.firstRespondedAt) OR IS_NULL(c.sla.firstRespondedAt)) AND c.sla.firstResponseDueAt < @now) OR c.sla.resolutionDueAt < @now))";
            where.Add(breached ? isBreached : $"NOT {isBreached}");
            parameters["@active"] = TicketStatuses.Active.ToArray();
            // Dates are stored as ISO-8601 UTC strings, so a string comparison against the same format is chronological.
            parameters["@now"] = JsonSerializer.Serialize(clock.UtcNow.ToUniversalTime(), CosmosContainers.SerializerOptions).Trim('"');
        }

        var order = q.Sort switch
        {
            "createdAt" => "c.createdAt DESC",
            "priority" => "c.priority ASC",
            "dueAt" => "c.sla.resolutionDueAt ASC",
            _ => "c.updatedAt DESC"
        };
        var query = new QueryDefinition($"SELECT * FROM c WHERE {string.Join(" AND ", where)} ORDER BY {order}");
        foreach (var (name, value) in parameters) query = query.WithParameter(name, value);
        return CosmosErrors.ReadPage<Ticket>(C, query, tenantId, q.PageSize, q.ContinuationToken, ct);
    }

    public Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken ct = default) =>
        CosmosErrors.Map(async () => (await C.CreateItemAsync(entry, new PartitionKey(entry.TenantId), cancellationToken: ct)).Resource,
            "timeline entry", entry.Id);

    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(string tenantId, string ticketId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.ticketId = @ticketId AND (c.docType = @comment OR c.docType = @event) ORDER BY c.createdAt ASC")
            .WithParameter("@ticketId", ticketId)
            .WithParameter("@comment", DocTypes.Comment)
            .WithParameter("@event", DocTypes.Event);
        return await CosmosErrors.ReadAll<TimelineEntry>(C, query, tenantId, ct);
    }

    public async Task<long> NextNumberAsync(string tenantId, string counterName, CancellationToken ct = default)
    {
        var id = Counter.IdFor(counterName);
        var pk = new PartitionKey(tenantId);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var patched = await C.PatchItemAsync<Counter>(id, pk,
                    [PatchOperation.Increment("/value", 1), PatchOperation.Set("/updatedAt", clock.UtcNow)], cancellationToken: ct);
                return patched.Resource.Value;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound && attempt < 3)
            {
                var now = clock.UtcNow;
                try
                {
                    var created = await C.CreateItemAsync(new Counter
                    {
                        Id = id, TenantId = tenantId, Value = 1, CreatedAt = now, CreatedBy = "system", UpdatedAt = now, UpdatedBy = "system"
                    }, pk, cancellationToken: ct);
                    return created.Resource.Value;
                }
                catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
                {
                    // Another request created it first; increment it on the next pass.
                }
            }
        }
    }

    public Task<IntakeReceipt?> GetIntakeReceiptAsync(string tenantId, string eventId, CancellationToken ct = default) =>
        CosmosErrors.ReadOrNull<IntakeReceipt>(C, tenantId, IntakeReceipt.IdFor(eventId), ct);

    public async Task<bool> TryCreateIntakeReceiptAsync(IntakeReceipt receipt, CancellationToken ct = default)
    {
        try
        {
            await C.CreateItemAsync(receipt, new PartitionKey(receipt.TenantId), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}

public sealed class CosmosKbRepository(CosmosContainers containers) : IKbRepository
{
    private Container C => containers.Kb;

    /// <summary>Every article field except the vector, so list queries do not ship 1,536 floats per row.</summary>
    private static readonly string ListProjection = string.Join(", ",
        typeof(KbArticle).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(KbArticle.Embedding))
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .Distinct()
            .Select(name => $"c.{name}"));

    public async Task<KbArticle?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await CosmosErrors.ReadOrNull<KbArticle>(C, tenantId, id, ct) is { DocType: DocTypes.KbArticle } a ? a : null;

    public Task<KbArticle> CreateAsync(KbArticle article, CancellationToken ct = default) =>
        CosmosErrors.Map(async () => (await C.CreateItemAsync(article, new PartitionKey(article.TenantId), cancellationToken: ct)).Resource,
            "KB article", article.Id);

    public Task<KbArticle> ReplaceAsync(KbArticle article, string? ifMatch, CancellationToken ct = default) =>
        CosmosErrors.Map(async () => (await C.ReplaceItemAsync(article, article.Id, new PartitionKey(article.TenantId),
            CosmosErrors.IfMatch(ifMatch), ct)).Resource, "KB article", article.Id);

    public Task<Page<KbArticle>> QueryAsync(string tenantId, KbQuery q, CancellationToken ct = default)
    {
        var where = new List<string> { "c.docType = @docType" };
        var query = new Dictionary<string, object> { ["@docType"] = DocTypes.KbArticle };
        if (q.Status is { } s) { where.Add("c.status = @status"); query["@status"] = s; }
        if (q.Category is { } cat) { where.Add("c.category = @category"); query["@category"] = cat; }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("(CONTAINS(c.number, @text, true) OR CONTAINS(c.title, @text, true) OR CONTAINS(c.summary, @text, true) " +
                      "OR EXISTS(SELECT VALUE t FROM t IN c.tags WHERE CONTAINS(t, @text, true)))");
            query["@text"] = q.Text.Trim();
        }
        var definition = new QueryDefinition(
            $"SELECT {ListProjection} FROM c WHERE {string.Join(" AND ", where)} ORDER BY c.updatedAt DESC");
        foreach (var (name, value) in query) definition = definition.WithParameter(name, value);
        return CosmosErrors.ReadPage<KbArticle>(C, definition, tenantId, q.PageSize, q.ContinuationToken, ct);
    }

    public async Task<IReadOnlyList<KbArticle>> GetManyAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var query = new QueryDefinition($"SELECT {ListProjection} FROM c WHERE c.docType = @docType AND ARRAY_CONTAINS(@ids, c.id)")
            .WithParameter("@docType", DocTypes.KbArticle)
            .WithParameter("@ids", ids.Distinct().ToArray());
        return await CosmosErrors.ReadAll<KbArticle>(C, query, tenantId, ct);
    }

    public Task AddVersionAsync(KbVersion version, CancellationToken ct = default) =>
        C.UpsertItemAsync(version, new PartitionKey(version.TenantId), cancellationToken: ct);

    public async Task<IReadOnlyList<KbVersion>> GetVersionsAsync(string tenantId, string articleId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.docType = @docType AND c.articleId = @articleId ORDER BY c.version DESC")
            .WithParameter("@docType", DocTypes.KbVersion)
            .WithParameter("@articleId", articleId);
        return await CosmosErrors.ReadAll<KbVersion>(C, query, tenantId, ct);
    }

    public async Task<IReadOnlyList<KbArticle>> FindNeedingEmbeddingAsync(int maxAttempts, int limit, CancellationToken ct = default)
    {
        // Cross-partition by design: the sweep is a background job over all tenants.
        var query = new QueryDefinition(
                $"SELECT {ListProjection} FROM c WHERE c.docType = @docType AND (c.embeddingStatus = @pending OR (c.embeddingStatus = @failed AND c.embeddingAttempts < @max))")
            .WithParameter("@docType", DocTypes.KbArticle)
            .WithParameter("@pending", EmbeddingStatuses.Pending)
            .WithParameter("@failed", EmbeddingStatuses.Failed)
            .WithParameter("@max", maxAttempts);
        return await CosmosErrors.ReadAll<KbArticle>(C, query, null, ct, limit);
    }
}

public sealed class CosmosAttachmentRepository(CosmosContainers containers) : IAttachmentRepository
{
    private Container C => containers.Attachments;

    public Task<Attachment?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        CosmosErrors.ReadOrNull<Attachment>(C, tenantId, id, ct);

    public Task<Attachment> CreateAsync(Attachment attachment, CancellationToken ct = default) =>
        CosmosErrors.Map(async () => (await C.CreateItemAsync(attachment, new PartitionKey(attachment.TenantId), cancellationToken: ct)).Resource,
            "attachment", attachment.Id);

    public Task<Attachment> ReplaceAsync(Attachment attachment, string? ifMatch, CancellationToken ct = default) =>
        CosmosErrors.Map(async () => (await C.ReplaceItemAsync(attachment, attachment.Id, new PartitionKey(attachment.TenantId),
            CosmosErrors.IfMatch(ifMatch), ct)).Resource, "attachment", attachment.Id);

    public async Task<IReadOnlyList<Attachment>> ListForTicketAsync(string tenantId, string ticketId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.ticketId = @ticketId ORDER BY c.createdAt ASC")
            .WithParameter("@ticketId", ticketId);
        return await CosmosErrors.ReadAll<Attachment>(C, query, tenantId, ct);
    }
}
