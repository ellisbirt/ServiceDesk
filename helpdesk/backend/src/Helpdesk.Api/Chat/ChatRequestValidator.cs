using Helpdesk.Shared.Contracts;

namespace Helpdesk.Api.Chat;

/// <summary>Request rules shared by the REST endpoint and the SignalR hub.</summary>
public static class ChatRequestValidator
{
    public const int MaxMessageLength = 4000;
    public const int MaxHistoryMessages = 20;

    /// <summary>A hard cap on references per message, whatever <c>Attachments:MaxFilesPerMessage</c> allows, so a request cannot be padded.</summary>
    public const int MaxAttachmentReferences = 50;

    /// <summary>A signature is about 110 characters; anything much longer is not one.</summary>
    public const int MaxSignatureLength = 256;

    /// <summary>
    /// Largest JSON body of one hub message when a request is sent in parts (<c>SendStreamed</c>): a single message
    /// at the length limit with every character escaped to six bytes (<c>\u00XX</c>, the JSON worst case), plus a
    /// little for the envelope. The SignalR receive limit must be at least this, or a valid part would make the
    /// server close the connection. A whole request (<c>Send</c>) can be 21 times this, which is why large ones are
    /// split.
    /// </summary>
    public const int MaxPartBytes = MaxMessageLength * 6 + 1024;

    /// <summary>Returns the validation errors keyed by field, or null when the request is valid.</summary>
    public static Dictionary<string, string[]>? Validate(ChatMessageRequest? request)
    {
        if (request is null)
        {
            return new() { ["request"] = ["A request body is required."] };
        }
        if (request.ConversationId == Guid.Empty)
        {
            return new() { ["conversationId"] = ["A non-empty conversationId is required."] };
        }
        if (string.IsNullOrWhiteSpace(request.MessageText) || request.MessageText.Length > MaxMessageLength)
        {
            return new() { ["messageText"] = [$"Message must be between 1 and {MaxMessageLength} characters."] };
        }
        if (request.ConversationHistory is null
            || request.ConversationHistory.Count > MaxHistoryMessages
            || request.ConversationHistory.Any(message =>
                (message.Role != "user" && message.Role != "assistant")
                || string.IsNullOrWhiteSpace(message.Content)
                || message.Content.Length > MaxMessageLength
                || message.Signature?.Length > MaxSignatureLength))
        {
            return new()
            {
                ["conversationHistory"] = [$"History must contain at most {MaxHistoryMessages} user/assistant messages, each between 1 and {MaxMessageLength} characters."]
            };
        }
        if (BadReferences(request.Attachments) || request.ConversationHistory.Any(m => BadReferences(m.Attachments)))
        {
            return new() { ["attachments"] = ["Attachment references are malformed."] };
        }
        return null;
    }

    private static bool BadReferences(IReadOnlyList<AttachmentRef>? references) =>
        references is not null
        && (references.Count > MaxAttachmentReferences
            || references.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Token) || r.Token.Length > 1024
                || r.Name is null || r.Name.Length > 255));
}
