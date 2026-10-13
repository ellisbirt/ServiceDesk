using AgentDesk.Api.Endpoints;
using AgentDesk.Api.Security;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using Microsoft.AspNetCore.SignalR;

namespace AgentDesk.Api.Realtime;

/// <summary>
/// Live updates for the agent console. Clients only listen; every change goes through the REST API. On connect a client
/// joins its tenant's group, so it only ever receives its own tenant's events.
/// Events: <c>ticketCreated(ticket)</c>, <c>ticketUpdated(ticket)</c>, <c>timelineEntryAdded(ticketId, entry)</c>,
/// <c>kbChanged(article)</c>.
/// </summary>
public sealed class TicketsHub : Hub
{
    public const string Path = "/hubs/tickets";

    public static string TenantGroup(string tenantId) => $"tickets:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        if (Context.User is { } user && AgentDeskClaims.TenantId(user) is { } tenant)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenant));
        }
        await base.OnConnectedAsync();
    }
}

public static class HubEvents
{
    public const string TicketCreated = "ticketCreated";
    public const string TicketUpdated = "ticketUpdated";
    public const string TimelineEntryAdded = "timelineEntryAdded";
    public const string KbChanged = "kbChanged";
}

/// <summary>
/// Broadcasts to the tenant group. Every connected client today is an agent, so internal notes are sent as-is; a future
/// requester portal must use a separate group that receives public entries only.
/// </summary>
public sealed class SignalRTicketNotifier(IHubContext<TicketsHub> hub, ILogger<SignalRTicketNotifier> logger) : ITicketNotifier
{
    public Task TicketCreatedAsync(Ticket ticket, CancellationToken ct = default) =>
        SendAsync(ticket.TenantId, HubEvents.TicketCreated, [ticket], ct);

    public Task TicketUpdatedAsync(Ticket ticket, CancellationToken ct = default) =>
        SendAsync(ticket.TenantId, HubEvents.TicketUpdated, [ticket], ct);

    public Task TimelineEntryAddedAsync(Ticket ticket, TimelineEntry entry, CancellationToken ct = default) =>
        SendAsync(ticket.TenantId, HubEvents.TimelineEntryAdded, [ticket.Id, entry], ct);

    public Task KbChangedAsync(KbArticle article, CancellationToken ct = default) =>
        SendAsync(article.TenantId, HubEvents.KbChanged, [KbArticleDto.From(article)], ct);

    private async Task SendAsync(string tenantId, string method, object?[] args, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(TicketsHub.TenantGroup(tenantId)).SendCoreAsync(method, args, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A live-update failure must never fail the write that triggered it; clients refetch on reconnect.
            logger.LogWarning(ex, "Failed to broadcast {Method} to tenant {TenantId}", method, tenantId);
        }
    }
}
