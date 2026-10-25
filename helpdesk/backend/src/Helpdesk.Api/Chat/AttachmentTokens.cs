using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Proof that the server stored an upload for this tenant, user and conversation. The token is an HMAC over the attachment
/// id with the conversation signing keys, so a client cannot invent a reference or use one from another conversation.
/// The messages themselves are not re-signed: a client may attach any of its own uploads to any of its own messages, and
/// the descriptive fields in a reference are ignored in favour of what storage holds.
/// </summary>
public static class AttachmentTokens
{
    public const string Purpose = "attachment";

    public static AttachmentRef ToRef(IConversationSigner signer, string tenantId, string userId, Guid conversationId, AttachmentInfo info) => new()
    {
        Id = info.Id,
        Name = info.FileName,
        ContentType = info.ContentType,
        Size = info.Size,
        Sha256 = info.Sha256,
        Kind = info.Kind,
        Token = signer.SignToken(Purpose, tenantId, userId, conversationId, info.Id.ToString("N"))
    };

    public static bool IsValid(IConversationSigner signer, string tenantId, string userId, Guid conversationId, AttachmentRef reference) =>
        signer.VerifyToken(Purpose, tenantId, userId, conversationId, reference.Token) == reference.Id.ToString("N");

    /// <summary>The ids of the references whose tokens are genuine, in order and without repeats.</summary>
    public static IReadOnlyList<Guid> ValidIds(
        IConversationSigner signer, string tenantId, string userId, Guid conversationId, IEnumerable<AttachmentRef>? references) =>
        [.. (references ?? []).Where(r => IsValid(signer, tenantId, userId, conversationId, r)).Select(r => r.Id).Distinct()];
}
