using System.Collections.Concurrent;
using System.Threading.Channels;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Chat;

/// <param name="ConnectionId">The hub connection that sent the job. When it closes, the job is abandoned.</param>
public sealed record ChatJob(
    Guid RequestId, string TenantId, string UserId, ChatMessageRequest Request, string? ConnectionId = null,
    string? UserAgent = null)
{
    /// <summary>The SignalR group every connection of this user joins, so replies survive a reconnect.</summary>
    public string Group => ChatGroups.For(TenantId, UserId);

    // Cancelled when nobody is waiting for the result any more: the connection that asked has gone, or the client
    // asked to cancel. A plain source (no timer, no wait handle) owns nothing that needs disposing.
    private readonly CancellationTokenSource _abandoned = new();

    public CancellationToken Abandoned => _abandoned.Token;

    public bool IsAbandoned => _abandoned.IsCancellationRequested;

    public void Abandon() => _abandoned.Cancel();
}

public static class ChatGroups
{
    // Unambiguous because tenant and user ids cannot contain the separator (enforced by the authorization policy
    // and by JwtTenantProvider.TenantId / UserId).
    public static string For(string tenantId, string userId) =>
        $"chat{JwtTenantProvider.KeySeparator}{tenantId}{JwtTenantProvider.KeySeparator}{userId}";
}

public enum EnqueueResult { Accepted, AlreadyRunning, Busy, TenantBusy }

/// <summary>
/// In-memory hand-off between the hub (which must return quickly) and <see cref="ChatJobProcessor"/>.
/// One job per (tenant, user, conversation) may be in flight at a time, and one tenant may have at most
/// <see cref="ChatOptions.MaxJobsPerTenant"/> in flight. In memory means jobs are lost on restart and are not
/// shared across instances; the client is told when its job fails, but not when the process dies.
/// </summary>
public sealed class ChatJobQueue(IOptions<ChatOptions> options, TenantJobLimiter tenants)
{
    private sealed record Entry(ChatJob Job, IDisposable TenantLease);

    private readonly Channel<ChatJob> _channel = Channel.CreateBounded<ChatJob>(
        new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly ConcurrentDictionary<(string Tenant, string User, Guid Conversation), Entry> _active = new();

    public ChannelReader<ChatJob> Reader => _channel.Reader;

    public EnqueueResult TryEnqueue(ChatJob job)
    {
        var key = KeyOf(job);
        // "This conversation is busy" is the more useful answer when both are true.
        if (_active.ContainsKey(key)) return EnqueueResult.AlreadyRunning;
        if (tenants.TryAcquire(job.TenantId) is not { } lease) return EnqueueResult.TenantBusy;

        if (!_active.TryAdd(key, new Entry(job, lease)))
        {
            lease.Dispose();
            return EnqueueResult.AlreadyRunning;
        }

        // TryWrite returns false when the bounded channel is full (Wait mode never blocks here) or the queue has
        // been closed because the service is shutting down.
        if (_channel.Writer.TryWrite(job)) return EnqueueResult.Accepted;

        Complete(job);
        return EnqueueResult.Busy;
    }

    /// <summary>Marks the job finished so the conversation, and the tenant's place, can be used again.</summary>
    public void Complete(ChatJob job)
    {
        if (_active.TryRemove(KeyOf(job), out var entry)) entry.TenantLease.Dispose();
    }

    /// <summary>Abandons every in-flight job that was sent by this connection. Returns how many.</summary>
    public int CancelConnection(string connectionId)
    {
        var cancelled = 0;
        foreach (var entry in _active.Values)
        {
            if (entry.Job.ConnectionId != connectionId || entry.Job.IsAbandoned) continue;
            entry.Job.Abandon();
            cancelled++;
        }
        return cancelled;
    }

    /// <summary>Abandons the caller's own in-flight job for a conversation. Returns false when there is none.</summary>
    public bool CancelConversation(string tenantId, string userId, Guid conversationId)
    {
        if (!_active.TryGetValue((tenantId, userId, conversationId), out var entry) || entry.Job.IsAbandoned) return false;
        entry.Job.Abandon();
        return true;
    }

    /// <summary>Stops accepting jobs (the service is shutting down). Jobs already queued can still be read.</summary>
    public void CloseForWrites() => _channel.Writer.TryComplete();

    private static (string, string, Guid) KeyOf(ChatJob job) => (job.TenantId, job.UserId, job.Request.ConversationId);
}
