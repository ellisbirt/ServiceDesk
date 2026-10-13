using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helpdesk.Api.Chat;

/// <summary>
/// An escalation that is waiting for its follow-up form. Nothing is queued until the form is submitted, and the
/// server keeps no state in between, so the reply hands this to the client as a signed token.
/// </summary>
public sealed record PendingEscalation
{
    /// <summary>The ticket event's id, fixed up front so a re-submitted form raises the same (deduplicated) ticket.</summary>
    [JsonPropertyName("eventId")]
    public required Guid EventId { get; init; }

    /// <summary>The original request, already redacted with the Queue policy.</summary>
    [JsonPropertyName("payload")]
    public required string Payload { get; init; }

    [JsonPropertyName("expiresUtc")]
    public required DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>Issues and checks escalation tokens. A token is only valid for the tenant, user and conversation it was issued to.</summary>
public sealed class EscalationTokens(IConversationSigner signer, TimeProvider time)
{
    public const string Purpose = "escalation";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public string Issue(string tenantId, string userId, Guid conversationId, string queuePayload)
    {
        var pending = new PendingEscalation
        {
            EventId = Guid.NewGuid(),
            Payload = queuePayload,
            ExpiresUtc = time.GetUtcNow() + Lifetime
        };
        return signer.SignToken(Purpose, tenantId, userId, conversationId, JsonSerializer.Serialize(pending));
    }

    /// <summary>The pending escalation, or null if the token is forged, expired or issued to someone else.</summary>
    public PendingEscalation? Redeem(string tenantId, string userId, Guid conversationId, string? token)
    {
        if (signer.VerifyToken(Purpose, tenantId, userId, conversationId, token) is not { } json) return null;

        PendingEscalation? pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingEscalation>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        return pending is not null && pending.ExpiresUtc > time.GetUtcNow() ? pending : null;
    }
}
