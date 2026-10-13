using AgentDesk.Core.Domain;

namespace AgentDesk.Core.Rules;

public static class TicketStateMachine
{
    private static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        [TicketStatuses.New] = [TicketStatuses.Open, TicketStatuses.Pending, TicketStatuses.OnHold, TicketStatuses.Resolved, TicketStatuses.Cancelled],
        [TicketStatuses.Open] = [TicketStatuses.Pending, TicketStatuses.OnHold, TicketStatuses.Resolved, TicketStatuses.Cancelled],
        [TicketStatuses.Pending] = [TicketStatuses.Open, TicketStatuses.OnHold, TicketStatuses.Resolved, TicketStatuses.Cancelled],
        [TicketStatuses.OnHold] = [TicketStatuses.Open, TicketStatuses.Pending, TicketStatuses.Resolved, TicketStatuses.Cancelled],
        [TicketStatuses.Resolved] = [TicketStatuses.Open, TicketStatuses.Closed],
        [TicketStatuses.Closed] = [],
        [TicketStatuses.Cancelled] = []
    };

    public static IReadOnlyList<string> AllowedFrom(string status) =>
        Transitions.TryGetValue(status, out var next) ? next : [];

    public static bool CanTransition(string from, string to) => AllowedFrom(from).Contains(to);

    public static bool IsTerminal(string status) => status is TicketStatuses.Closed or TicketStatuses.Cancelled;
}

/// <summary>ITIL-style impact × urgency matrix. 1 is highest for both.</summary>
public static class PriorityMatrix
{
    public static string Compute(int impact, int urgency)
    {
        if (impact is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(impact), "Impact must be 1, 2 or 3.");
        if (urgency is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(urgency), "Urgency must be 1, 2 or 3.");
        return (impact + urgency) switch
        {
            2 => Priorities.P1,
            3 => Priorities.P2,
            4 => Priorities.P3,
            _ => Priorities.P4
        };
    }

    /// <summary>Maps the intake classifier's severity to an impact/urgency pair.</summary>
    public static (int Impact, int Urgency) FromSeverity(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" => (1, 1),
        "high" => (2, 1),
        "medium" => (2, 2),
        _ => (3, 3)
    };
}

public sealed record SlaTarget(TimeSpan FirstResponse, TimeSpan Resolution);

public sealed class SlaOptions
{
    public Dictionary<string, SlaTarget> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Priorities.P1] = new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(4)),
        [Priorities.P2] = new(TimeSpan.FromHours(1), TimeSpan.FromHours(8)),
        [Priorities.P3] = new(TimeSpan.FromHours(4), TimeSpan.FromHours(24)),
        [Priorities.P4] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(72))
    };
}

/// <summary>Calendar-time SLA. Business hours and pause-on-pending are out of scope for now.</summary>
public sealed class SlaCalculator(SlaOptions options)
{
    public SlaTarget TargetFor(string priority) =>
        options.Targets.TryGetValue(priority, out var target) ? target : options.Targets[Priorities.P4];

    /// <summary>Sets due dates from the ticket's creation time and current priority, then re-evaluates breaches.</summary>
    public void Apply(Ticket ticket, DateTimeOffset now)
    {
        var target = TargetFor(ticket.Priority);
        ticket.Sla.FirstResponseDueAt = ticket.CreatedAt + target.FirstResponse;
        ticket.Sla.ResolutionDueAt = ticket.CreatedAt + target.Resolution;
        Evaluate(ticket, now);
    }

    public static void Evaluate(Ticket ticket, DateTimeOffset now)
    {
        var sla = ticket.Sla;
        var responded = sla.FirstRespondedAt;
        sla.FirstResponseBreached = responded is { } r ? r > sla.FirstResponseDueAt : now > sla.FirstResponseDueAt;

        var resolvedAt = ticket.Resolution?.ResolvedAt;
        if (resolvedAt is { } done)
            sla.ResolutionBreached = done > sla.ResolutionDueAt;
        else if (TicketStatuses.Active.Contains(ticket.Status))
            sla.ResolutionBreached = now > sla.ResolutionDueAt;
    }
}

public static class Numbering
{
    public static string Ticket(string type, long value) => $"{TicketTypes.NumberPrefix(type)}{value:D6}";
    public static string Kb(long value) => $"KB{value:D7}";
}
