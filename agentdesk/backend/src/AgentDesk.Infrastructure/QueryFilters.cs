using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;

namespace AgentDesk.Infrastructure;

/// <summary>One definition of the ticket and KB list filters, applied in memory and translated to Cosmos DB SQL.</summary>
internal static class QueryFilters
{
    public static IEnumerable<Ticket> Apply(IEnumerable<Ticket> source, TicketQuery q, DateTimeOffset now)
    {
        var items = source;
        if (q.Statuses is { Count: > 0 } statuses) items = items.Where(t => statuses.Contains(t.Status));
        if (q.Priority is { } p) items = items.Where(t => t.Priority == p);
        if (q.Unassigned) items = items.Where(t => t.Assignee is null);
        else if (q.AssigneeId is { } a) items = items.Where(t => t.Assignee?.Id == a);
        if (q.AssignmentGroup is { } g) items = items.Where(t => t.AssignmentGroup == g);
        if (q.Category is { } c) items = items.Where(t => t.Category == c);
        if (q.RequesterId is { } r) items = items.Where(t => t.Requester.Id == r);
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var text = q.Text.Trim();
            items = items.Where(t => t.Number.Contains(text, StringComparison.OrdinalIgnoreCase)
                                     || t.Subject.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        if (q.Breached is { } breached) items = items.Where(t => IsBreached(t, now) == breached);

        return q.Sort switch
        {
            "createdAt" => items.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id),
            "priority" => items.OrderBy(t => t.Priority, StringComparer.Ordinal).ThenByDescending(t => t.UpdatedAt).ThenBy(t => t.Id),
            "dueAt" => items.OrderBy(t => t.Sla.ResolutionDueAt).ThenBy(t => t.Id),
            _ => items.OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Id)
        };
    }

    /// <summary>An active ticket past a due date it has not met.</summary>
    public static bool IsBreached(Ticket t, DateTimeOffset now) =>
        TicketStatuses.Active.Contains(t.Status)
        && ((t.Sla.FirstRespondedAt is null && t.Sla.FirstResponseDueAt < now) || t.Sla.ResolutionDueAt < now);

    public static IEnumerable<KbArticle> Apply(IEnumerable<KbArticle> source, KbQuery q)
    {
        var items = source;
        if (q.Status is { } s) items = items.Where(a => a.Status == s);
        if (q.Category is { } c) items = items.Where(a => a.Category == c);
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var text = q.Text.Trim();
            items = items.Where(a => a.Number.Contains(text, StringComparison.OrdinalIgnoreCase)
                                     || a.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                                     || a.Summary.Contains(text, StringComparison.OrdinalIgnoreCase)
                                     || a.Tags.Any(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }
        return items.OrderByDescending(a => a.UpdatedAt).ThenBy(a => a.Id);
    }
}
