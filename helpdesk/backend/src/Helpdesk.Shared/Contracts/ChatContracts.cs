using System.Text.Json.Serialization;

namespace Helpdesk.Shared.Contracts;

public static class ActionLanes
{
    public const string PolicyViolation = "POLICY_VIOLATION";
    public const string PublicTech = "PUBLIC_TECH";
    public const string PublicSearch = "PUBLIC_SEARCH";
    /// <summary>Company-specific support, or an in-scope issue the requester wants a service-desk ticket for.</summary>
    public const string PrivateCorp = "PRIVATE_CORP";
    public const string NoMatch = "NO_MATCH";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PolicyViolation, PublicTech, PublicSearch, PrivateCorp, NoMatch
    };
}

/// <summary>What kind of file an attachment is, which decides how it is shown to the model.</summary>
public static class AttachmentKinds
{
    /// <summary>Plain text: txt, log, csv, json, md.</summary>
    public const string Text = "text";
    public const string Image = "image";
    public const string Pdf = "pdf";
    /// <summary>Modern Office documents: docx, xlsx, pptx.</summary>
    public const string Office = "office";
}

/// <summary>
/// A file the user uploaded to a conversation, as returned by <c>POST /api/v1/chat/attachments</c>. The client sends it
/// back unchanged on the message that uses it and on that message in the stored history. The server never trusts the
/// descriptive fields: it checks <see cref="Token"/> and reads the file's real details from storage.
/// </summary>
public record AttachmentRef
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("size")]
    public required long Size { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>One of <see cref="AttachmentKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Proof that the server stored this file for this user and conversation. Opaque; send it back as received.</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }
}

public record ChatMessageRequest
{
    [JsonPropertyName("messageText")]
    public required string MessageText { get; init; }

    [JsonPropertyName("conversationId")]
    public required Guid ConversationId { get; init; }

    [JsonPropertyName("conversationHistory")]
    public IReadOnlyList<ChatHistoryMessage> ConversationHistory { get; init; } = [];

    /// <summary>Files uploaded to this conversation for this message.</summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<AttachmentRef> Attachments { get; init; } = [];
}

public record ChatHistoryMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary>
    /// Proof that the server produced (or received) exactly this message at this place in this conversation. The
    /// server returns it in <c>HelpdeskApiResponse.UserSignature</c> / <c>AssistantSignature</c>; the client stores it
    /// and sends it back unchanged. History without a valid signature is not trusted and is dropped.
    /// </summary>
    [JsonPropertyName("sig")]
    public string? Signature { get; init; }

    /// <summary>Files that were attached to this message; keep them on the stored history message and send them back.</summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<AttachmentRef> Attachments { get; init; } = [];

    // Records compare lists by reference; two messages with the same files are the same message.
    public virtual bool Equals(ChatHistoryMessage? other) =>
        other is not null && Role == other.Role && Content == other.Content && Signature == other.Signature
        && (Attachments ?? []).SequenceEqual(other.Attachments ?? []);

    public override int GetHashCode() => HashCode.Combine(Role, Content, Signature, (Attachments ?? []).Count);
}

/// <summary>
/// One piece of a request uploaded with the hub's <c>SendStreamed</c> method. SignalR limits the size of every
/// hub message, so a long conversation is sent as several small parts instead of one large one.
/// The first part carries <see cref="ConversationId"/> and <see cref="MessageText"/> and nothing else; every
/// following part carries exactly one <see cref="History"/> message, oldest first.
/// </summary>
public record ChatRequestPart
{
    [JsonPropertyName("conversationId")]
    public Guid? ConversationId { get; init; }

    [JsonPropertyName("messageText")]
    public string? MessageText { get; init; }

    /// <summary>Carried by the first part only, with <see cref="ConversationId"/> and <see cref="MessageText"/>.</summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<AttachmentRef>? Attachments { get; init; }

    [JsonPropertyName("history")]
    public ChatHistoryMessage? History { get; init; }
}

public record HelpdeskApiResponse
{
    [JsonPropertyName("conversationId")]
    public required Guid ConversationId { get; init; }

    [JsonPropertyName("actionLane")]
    public required string ActionLane { get; init; }

    [JsonPropertyName("responseText")]
    public required string ResponseText { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>Signature for the user's message of this turn; attach it to that message in the stored transcript.</summary>
    [JsonPropertyName("userSig")]
    public string? UserSignature { get; init; }

    /// <summary>Signature for <see cref="ResponseText"/>; attach it to the assistant message in the stored transcript.</summary>
    [JsonPropertyName("assistantSig")]
    public string? AssistantSignature { get; init; }
}

public static class ChatEventTypes
{
    /// <summary>The request was accepted and is being processed.</summary>
    public const string Started = "started";
    /// <summary>A fragment of the reply text (<see cref="ChatStreamEvent.Text"/>).</summary>
    public const string Delta = "delta";
    /// <summary>The model is rate limited; processing resumes after <see cref="ChatStreamEvent.RetryAfterSeconds"/>.</summary>
    public const string RateLimited = "rateLimited";
    /// <summary>Liveness signal while work is in flight, with <see cref="ChatStreamEvent.ElapsedSeconds"/>.</summary>
    public const string Heartbeat = "heartbeat";
    /// <summary>The final, authoritative reply (<see cref="ChatStreamEvent.Response"/>).</summary>
    public const string Completed = "completed";
    /// <summary>The request failed (<see cref="ChatStreamEvent.Error"/> and <see cref="ChatStreamEvent.ErrorCode"/>).</summary>
    public const string Failed = "failed";
}

/// <summary>One server-to-client message on the chat hub. Every event is correlated by conversation and request.</summary>
public record ChatStreamEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("conversationId")]
    public required Guid ConversationId { get; init; }

    [JsonPropertyName("requestId")]
    public required Guid RequestId { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("retryAfterSeconds")]
    public double? RetryAfterSeconds { get; init; }

    [JsonPropertyName("attempt")]
    public int? Attempt { get; init; }

    [JsonPropertyName("elapsedSeconds")]
    public double? ElapsedSeconds { get; init; }

    [JsonPropertyName("response")]
    public HelpdeskApiResponse? Response { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Returned from the hub's Send method. Either the request was queued (<see cref="RequestId"/> is set) or it was
/// rejected (<see cref="ErrorCode"/> and <see cref="Error"/> are set and nothing was queued).
/// Codes: invalid_request, rate_limited, in_progress, busy, forbidden.
/// </summary>
public record ChatAck
{
    [JsonPropertyName("requestId")]
    public Guid? RequestId { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static ChatAck Rejected(string code, string message) => new() { ErrorCode = code, Error = message };
}
