using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;

namespace AgentDesk.Infrastructure.InMemory;

internal static class Containers
{
    public const string Tickets = "tickets";
    public const string Kb = "kb";
    public const string Attachments = "attachments";
}

public sealed class InMemoryTicketRepository(InMemoryDocumentStore store, IClock clock) : ITicketRepository
{
    public Task<Ticket?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(store.Get<Ticket>(Containers.Tickets, tenantId, id) is { DocType: DocTypes.Ticket } t ? t : null);

    public Task<Ticket> CreateAsync(Ticket ticket, CancellationToken ct = default) =>
        Task.FromResult(store.Create(Containers.Tickets, ticket));

    public Task<Ticket> ReplaceAsync(Ticket ticket, string? ifMatch, CancellationToken ct = default) =>
        Task.FromResult(store.Replace(Containers.Tickets, ticket, ifMatch));

    public Task<Page<Ticket>> QueryAsync(string tenantId, TicketQuery query, CancellationToken ct = default)
    {
        var all = store.All<Ticket>(Containers.Tickets, tenantId, DocTypes.Ticket).ToList();
        return Task.FromResult(InMemoryPaging.Page(QueryFilters.Apply(all, query, clock.UtcNow), query.PageSize, query.ContinuationToken));
    }

    public Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken ct = default) =>
        Task.FromResult(store.Create(Containers.Tickets, entry));

    public Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(string tenantId, string ticketId, CancellationToken ct = default)
    {
        IReadOnlyList<TimelineEntry> entries = store.All<TimelineEntry>(Containers.Tickets, tenantId, DocTypes.Comment)
            .Concat(store.All<TimelineEntry>(Containers.Tickets, tenantId, DocTypes.Event))
            .Where(e => e.TicketId == ticketId)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToList();
        return Task.FromResult(entries);
    }

    public Task<long> NextNumberAsync(string tenantId, string counterName, CancellationToken ct = default)
    {
        var id = Counter.IdFor(counterName);
        var value = store.Update<Counter, long>(Containers.Tickets, tenantId, id, existing =>
        {
            var counter = existing ?? new Counter { Id = id, TenantId = tenantId, CreatedAt = clock.UtcNow, CreatedBy = "system" };
            counter.Value++;
            counter.UpdatedAt = clock.UtcNow;
            counter.UpdatedBy = "system";
            return (counter, counter.Value);
        });
        return Task.FromResult(value);
    }

    public Task<IntakeReceipt?> GetIntakeReceiptAsync(string tenantId, string eventId, CancellationToken ct = default) =>
        Task.FromResult(store.Get<IntakeReceipt>(Containers.Tickets, tenantId, IntakeReceipt.IdFor(eventId)));

    public Task<bool> TryCreateIntakeReceiptAsync(IntakeReceipt receipt, CancellationToken ct = default)
    {
        try
        {
            store.Create(Containers.Tickets, receipt);
            return Task.FromResult(true);
        }
        catch (DuplicateException)
        {
            return Task.FromResult(false);
        }
    }
}

public sealed class InMemoryKbRepository(InMemoryDocumentStore store) : IKbRepository
{
    public Task<KbArticle?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(store.Get<KbArticle>(Containers.Kb, tenantId, id) is { DocType: DocTypes.KbArticle } a ? a : null);

    public Task<KbArticle> CreateAsync(KbArticle article, CancellationToken ct = default) =>
        Task.FromResult(store.Create(Containers.Kb, article));

    public Task<KbArticle> ReplaceAsync(KbArticle article, string? ifMatch, CancellationToken ct = default) =>
        Task.FromResult(store.Replace(Containers.Kb, article, ifMatch));

    public Task<Page<KbArticle>> QueryAsync(string tenantId, KbQuery query, CancellationToken ct = default)
    {
        var all = store.All<KbArticle>(Containers.Kb, tenantId, DocTypes.KbArticle).ToList();
        foreach (var a in all) a.Embedding = null;
        return Task.FromResult(InMemoryPaging.Page(QueryFilters.Apply(all, query), query.PageSize, query.ContinuationToken));
    }

    public Task<IReadOnlyList<KbArticle>> GetManyAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        IReadOnlyList<KbArticle> found = ids.Distinct()
            .Select(id => store.Get<KbArticle>(Containers.Kb, tenantId, id))
            .Where(a => a is { DocType: DocTypes.KbArticle })
            .ToList()!;
        return Task.FromResult(found);
    }

    public Task AddVersionAsync(KbVersion version, CancellationToken ct = default)
    {
        store.Upsert(Containers.Kb, version);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KbVersion>> GetVersionsAsync(string tenantId, string articleId, CancellationToken ct = default)
    {
        IReadOnlyList<KbVersion> versions = store.All<KbVersion>(Containers.Kb, tenantId, DocTypes.KbVersion)
            .Where(v => v.ArticleId == articleId).OrderByDescending(v => v.Version).ToList();
        return Task.FromResult(versions);
    }

    public Task<IReadOnlyList<KbArticle>> FindNeedingEmbeddingAsync(int maxAttempts, int limit, CancellationToken ct = default)
    {
        IReadOnlyList<KbArticle> due = store.All<KbArticle>(Containers.Kb, null, DocTypes.KbArticle)
            .Where(a => a.EmbeddingStatus == EmbeddingStatuses.Pending
                        || (a.EmbeddingStatus == EmbeddingStatuses.Failed && a.EmbeddingAttempts < maxAttempts))
            .OrderBy(a => a.UpdatedAt).Take(limit).ToList();
        return Task.FromResult(due);
    }
}

public sealed class InMemoryAttachmentRepository(InMemoryDocumentStore store) : IAttachmentRepository
{
    public Task<Attachment?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(store.Get<Attachment>(Containers.Attachments, tenantId, id));

    public Task<Attachment> CreateAsync(Attachment attachment, CancellationToken ct = default) =>
        Task.FromResult(store.Create(Containers.Attachments, attachment));

    public Task<Attachment> ReplaceAsync(Attachment attachment, string? ifMatch, CancellationToken ct = default) =>
        Task.FromResult(store.Replace(Containers.Attachments, attachment, ifMatch));

    public Task<IReadOnlyList<Attachment>> ListForTicketAsync(string tenantId, string ticketId, CancellationToken ct = default)
    {
        IReadOnlyList<Attachment> items = store.All<Attachment>(Containers.Attachments, tenantId, DocTypes.Attachment)
            .Where(a => a.TicketId == ticketId).OrderBy(a => a.CreatedAt).ToList();
        return Task.FromResult(items);
    }
}
