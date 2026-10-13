using Helpdesk.Core;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Client-to-server: <see cref="Send"/>, which validates and queues the message and returns at once.
/// Server-to-client: <c>chatEvent</c> messages (<see cref="ChatStreamEvent"/>) sent to the user's group.
/// Authentication is the app's fallback policy (authenticated + tenant claim), applied to the negotiate request.
/// </summary>
public sealed class ChatHub(ChatJobQueue queue, ChatRequestLimiter limiter, ILogger<ChatHub> logger) : Hub
{
    public const string Path = "/hubs/chat";
    public const string ClientMethod = "chatEvent";

    public override async Task OnConnectedAsync()
    {
        var (tenantId, userId) = Identity();
        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroups.For(tenantId, userId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// A client that has gone cannot receive the reply, and nothing is replayed after a reconnect, so stop the work
    /// it asked for instead of paying for a model call nobody will read.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (queue.CancelConnection(Context.ConnectionId) is > 0 and var cancelled)
        {
            logger.LogInformation(
                "Abandoned {Count} chat job(s) because connection {ConnectionId} closed", cancelled, Context.ConnectionId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Stops the caller's own in-flight request for a conversation, for example when the user starts a new
    /// conversation while a reply is still being written. Returns false when there was nothing to cancel.
    /// </summary>
    public bool Cancel(Guid conversationId)
    {
        var (tenantId, userId) = Identity();
        return queue.CancelConversation(tenantId, userId, conversationId);
    }

    /// <summary>
    /// Rejections are returned in the ack rather than thrown: SignalR wraps a thrown <see cref="HubException"/>
    /// in boilerplate text, which clients would have to parse.
    /// </summary>
    public ChatAck Send(ChatMessageRequest request) => Accept(request);

    /// <summary>
    /// Same as <see cref="Send"/> for requests too large for one hub message: the client uploads a stream of
    /// <see cref="ChatRequestPart"/>s, each of which is its own message and so stays under the size limit.
    /// The parts are assembled here and go through the same validation and queueing as <see cref="Send"/>.
    /// </summary>
    public async Task<ChatAck> SendStreamed(IAsyncEnumerable<ChatRequestPart> parts)
    {
        Identity(); // fail before reading anything if the caller has no usable identity

        Guid? conversationId = null;
        string? messageText = null;
        var history = new List<ChatHistoryMessage>();
        var first = true;

        // Stop reading if the connection closes mid-upload.
        await foreach (var part in parts.WithCancellation(Context.ConnectionAborted))
        {
            if (first)
            {
                if (part.ConversationId is null || part.MessageText is null || part.History is not null)
                {
                    return ChatAck.Rejected("invalid_request", "The first part must carry conversationId and messageText only.");
                }
                (conversationId, messageText, first) = (part.ConversationId, part.MessageText, false);
                continue;
            }

            if (part.History is null || part.ConversationId is not null || part.MessageText is not null)
            {
                return ChatAck.Rejected("invalid_request", "Each part after the first must carry exactly one history message.");
            }
            if (history.Count >= ChatRequestValidator.MaxHistoryMessages)
            {
                // Stop reading: an over-long upload is rejected without buffering the rest of it.
                return ChatAck.Rejected("invalid_request", $"History must contain at most {ChatRequestValidator.MaxHistoryMessages} messages.");
            }
            history.Add(part.History);
        }

        if (first) return ChatAck.Rejected("invalid_request", "The request had no parts.");

        return Accept(new ChatMessageRequest
        {
            ConversationId = conversationId!.Value,
            MessageText = messageText!,
            ConversationHistory = history
        });
    }

    private ChatAck Accept(ChatMessageRequest request)
    {
        var (tenantId, userId) = Identity();

        if (ChatRequestValidator.Validate(request) is { } errors)
        {
            return ChatAck.Rejected("invalid_request", errors.Values.First()[0]);
        }
        if (!limiter.TryAcquire(tenantId, userId))
        {
            return ChatAck.Rejected("rate_limited", "You are sending messages too quickly. Please wait a moment.");
        }

        var userAgent = Context.GetHttpContext()?.Request.Headers.UserAgent.ToString();
        var job = new ChatJob(Guid.NewGuid(), tenantId, userId, request, Context.ConnectionId, userAgent);
        switch (queue.TryEnqueue(job))
        {
            case EnqueueResult.AlreadyRunning:
                return ChatAck.Rejected("in_progress", "This conversation is still working on your previous message.");
            case EnqueueResult.TenantBusy:
                Telemetry.TenantJobRejections.Add(1);
                logger.LogWarning("Tenant is at its limit of requests in flight; rejecting request for Tenant: {TenantId}", tenantId);
                return ChatAck.Rejected("tenant_busy", "Your organisation has too many requests in progress. Please try again shortly.");
            case EnqueueResult.Busy:
                logger.LogWarning("Chat job queue is full; rejecting request for Tenant: {TenantId}", tenantId);
                return ChatAck.Rejected("busy", "The service is busy. Please try again shortly.");
        }

        logger.LogInformation("Queued chat request {RequestId} for Tenant: {TenantId}, User: {UserId}", job.RequestId, tenantId, userId);
        return new ChatAck { RequestId = job.RequestId };
    }

    private (string TenantId, string UserId) Identity()
    {
        try
        {
            return (JwtTenantProvider.TenantId(Context.User), JwtTenantProvider.UserId(Context.User));
        }
        catch (UnauthorizedAccessException)
        {
            // The fallback policy already requires the tenant claim; this is defence in depth.
            throw new HubException("Tenant or identity context missing.");
        }
    }
}
