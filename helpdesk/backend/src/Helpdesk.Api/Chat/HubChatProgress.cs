using System.Diagnostics;
using System.Text;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Publishes a job's progress to the user's SignalR group. Delivery is best effort: a client that has gone away
/// must not fail the job, and the final <c>completed</c> event carries the whole reply anyway.
/// </summary>
public sealed class HubChatProgress(
    IHubContext<ChatHub> hub, ChatJob job, TimeSpan flushInterval, ILogger logger) : IChatProgress
{
    private readonly StringBuilder _pending = new();
    private readonly Lock _gate = new();
    private long _lastFlush = Stopwatch.GetTimestamp();

    public ValueTask OnTextAsync(string delta, CancellationToken ct)
    {
        string? toSend = null;
        lock (_gate)
        {
            _pending.Append(delta);
            if (Stopwatch.GetElapsedTime(_lastFlush) >= flushInterval) toSend = Take();
        }
        return toSend is null ? ValueTask.CompletedTask : new ValueTask(SendAsync(Event(ChatEventTypes.Delta) with { Text = toSend }, ct));
    }

    public async ValueTask OnRateLimitedAsync(TimeSpan retryAfter, int attempt, CancellationToken ct)
    {
        await FlushAsync(ct);
        await SendAsync(Event(ChatEventTypes.RateLimited) with
        {
            RetryAfterSeconds = Math.Round(retryAfter.TotalSeconds, 1),
            Attempt = attempt
        }, ct);
    }

    /// <summary>Sends any text still being held back for batching.</summary>
    public async ValueTask FlushAsync(CancellationToken ct)
    {
        string? toSend;
        lock (_gate) toSend = Take();
        if (toSend is not null) await SendAsync(Event(ChatEventTypes.Delta) with { Text = toSend }, ct);
    }

    public ChatStreamEvent Event(string type) => new()
    {
        Type = type,
        ConversationId = job.Request.ConversationId,
        RequestId = job.RequestId
    };

    public async Task SendAsync(ChatStreamEvent evt, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(job.Group).SendAsync(ChatHub.ClientMethod, evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not deliver {EventType} for request {RequestId}", evt.Type, job.RequestId);
        }
    }

    // Caller holds _gate.
    private string? Take()
    {
        _lastFlush = Stopwatch.GetTimestamp();
        if (_pending.Length == 0) return null;
        var text = _pending.ToString();
        _pending.Clear();
        return text;
    }
}
