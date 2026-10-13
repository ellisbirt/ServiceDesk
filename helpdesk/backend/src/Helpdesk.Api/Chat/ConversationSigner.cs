using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Helpdesk.Shared.Contracts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Keys for signing conversation history. A key is base64 for at least 32 random bytes, for example
/// <c>openssl rand -base64 32</c>. Sign with <see cref="ActiveKeyId"/>; every key in <see cref="Keys"/> still verifies,
/// so a key can be rotated by adding the new one, making it active, and removing the old one later.
/// <code>
/// "Conversation": { "ActiveKeyId": "2026-10", "Keys": { "2026-10": "&lt;base64&gt;", "2026-04": "&lt;base64&gt;" } }
/// </code>
/// </summary>
public sealed class ConversationSigningOptions
{
    public const string SectionName = "Conversation";
    public const int MinKeyBytes = 32;

    public string ActiveKeyId { get; set; } = "";
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Problems with the configuration. Never includes key material.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (id, value) in Keys)
        {
            if (!ConversationSigner.KeyId().IsMatch(id))
            {
                errors.Add($"Conversation:Keys:{id}: key ids must be 1-16 characters of letters, digits, '_' or '-'.");
            }
            if (!TryDecode(value, out _))
            {
                errors.Add($"Conversation:Keys:{id} must be base64 for at least {MinKeyBytes} bytes.");
            }
        }
        if (Keys.Count > 0 && (string.IsNullOrEmpty(ActiveKeyId) ? Keys.Count != 1 : !Keys.ContainsKey(ActiveKeyId)))
        {
            errors.Add("Conversation:ActiveKeyId must name one of Conversation:Keys (it may be omitted only when there is exactly one key).");
        }
        return errors;
    }

    internal static bool TryDecode(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            key = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        return key.Length >= MinKeyBytes;
    }
}

/// <summary>What <see cref="IConversationSigner.Verify"/> kept.</summary>
/// <param name="Messages">The trusted history: the longest chain of genuine messages ending at the newest one.</param>
/// <param name="Dropped">How many messages were sent but not trusted.</param>
/// <param name="Reason">Why the chain stopped: <c>unsigned</c>, <c>malformed</c>, <c>unknown_key</c>, <c>bad_signature</c> or <c>broken_chain</c>.</param>
public sealed record VerifiedHistory(IReadOnlyList<ChatHistoryMessage> Messages, int Dropped, string? Reason);

/// <summary>
/// Makes conversation history tamper-evident without the server remembering anything. The client keeps the
/// transcript and sends it back; every message carries a signature the server made when it produced or received
/// that message, and chains to the one before it. A client can therefore resend what really happened, in order, but
/// cannot invent, edit, reorder or splice messages (above all, cannot forge an "assistant" turn).
/// </summary>
public interface IConversationSigner
{
    /// <summary>Returns the trusted part of <paramref name="history"/>. Never throws for bad input; it drops it.</summary>
    VerifiedHistory Verify(string tenantId, string userId, Guid conversationId, IReadOnlyList<ChatHistoryMessage> history);

    /// <summary>Signs a finished turn so it can be sent back as history. <paramref name="verified"/> is what <see cref="Verify"/> returned.</summary>
    (string UserSignature, string AssistantSignature) SignTurn(
        string tenantId, string userId, Guid conversationId, IReadOnlyList<ChatHistoryMessage> verified,
        string userMessage, string assistantReply);

    /// <summary>Builds a signed history from scratch (tests, tooling, evals). Roles are <c>user</c> or <c>assistant</c>.</summary>
    IReadOnlyList<ChatHistoryMessage> SignHistory(
        string tenantId, string userId, Guid conversationId, IEnumerable<(string Role, string Content)> messages);
}

public sealed partial class ConversationSigner : IConversationSigner
{
    private const string Version = "v1";
    private const string NoPrevious = "-";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,16}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    internal static partial Regex KeyId();

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly string _activeKeyId;

    public ConversationSigner(IOptions<ConversationSigningOptions> options, IHostEnvironment env, ILogger<ConversationSigner> logger)
    {
        var o = options.Value;
        if (o.Validate().Count > 0)
        {
            throw new InvalidOperationException("The Conversation signing configuration is invalid: " + string.Join(" ", o.Validate()));
        }

        foreach (var (id, value) in o.Keys)
        {
            ConversationSigningOptions.TryDecode(value, out var key);
            _keys[id] = key;
        }

        if (_keys.Count == 0)
        {
            if (!env.IsDevelopment())
            {
                throw new InvalidOperationException("Conversation:Keys must contain at least one signing key outside Development.");
            }
            // A new random key per process: nothing to configure locally, but history from before a restart is not trusted.
            _keys["dev"] = RandomNumberGenerator.GetBytes(ConversationSigningOptions.MinKeyBytes);
            _activeKeyId = "dev";
            logger.LogWarning("No Conversation:Keys configured; using a temporary signing key. Conversation history will not survive an API restart.");
            return;
        }
        _activeKeyId = string.IsNullOrEmpty(o.ActiveKeyId) ? _keys.Keys.Single() : o.ActiveKeyId;
    }

    // ----- Signing -----

    public (string UserSignature, string AssistantSignature) SignTurn(
        string tenantId, string userId, Guid conversationId, IReadOnlyList<ChatHistoryMessage> verified,
        string userMessage, string assistantReply)
    {
        long seq = 0;
        var previous = NoPrevious;
        if (verified.Count > 0 && TryParse(verified[^1].Signature, out var last))
        {
            (seq, previous) = (last.Seq + 1, last.Mac);
        }

        var user = Sign(tenantId, userId, conversationId, seq, previous, "user", userMessage);
        TryParse(user, out var parsed);
        var assistant = Sign(tenantId, userId, conversationId, seq + 1, parsed.Mac, "assistant", assistantReply);
        return (user, assistant);
    }

    public IReadOnlyList<ChatHistoryMessage> SignHistory(
        string tenantId, string userId, Guid conversationId, IEnumerable<(string Role, string Content)> messages)
    {
        var result = new List<ChatHistoryMessage>();
        long seq = 0;
        var previous = NoPrevious;
        foreach (var (role, content) in messages)
        {
            var signature = Sign(tenantId, userId, conversationId, seq++, previous, role, content);
            TryParse(signature, out var parsed);
            previous = parsed.Mac;
            result.Add(new ChatHistoryMessage { Role = role, Content = content, Signature = signature });
        }
        return result;
    }

    private string Sign(string tenantId, string userId, Guid conversationId, long seq, string previous, string role, string content)
    {
        var mac = Compute(_keys[_activeKeyId], tenantId, userId, conversationId, seq, previous, role, content);
        return string.Join('.', Version, _activeKeyId, seq.ToString(CultureInfo.InvariantCulture), previous, WebEncoders.Base64UrlEncode(mac));
    }

    // ----- Verification -----

    public VerifiedHistory Verify(string tenantId, string userId, Guid conversationId, IReadOnlyList<ChatHistoryMessage> history)
    {
        if (history.Count == 0) return new([], 0, null);

        // Check every message on its own first, then keep the longest chain that ends at the newest message.
        var parsed = new Parsed?[history.Count];
        var failure = new string?[history.Count];
        for (var i = 0; i < history.Count; i++)
        {
            (parsed[i], failure[i]) = Check(tenantId, userId, conversationId, history[i]);
        }

        var last = history.Count - 1;
        if (parsed[last] is null) return new([], history.Count, failure[last]);

        var start = last;
        string? reason = null;
        while (start > 0)
        {
            if (parsed[start - 1] is not { } before)
            {
                reason = failure[start - 1];
                break;
            }
            var after = parsed[start]!.Value;
            if (after.Seq != before.Seq + 1 || !string.Equals(after.Previous, before.Mac, StringComparison.Ordinal))
            {
                reason = "broken_chain";
                break;
            }
            start--;
        }
        return new(history.Skip(start).ToList(), start, start == 0 ? null : reason);
    }

    private (Parsed? Parsed, string? Failure) Check(string tenantId, string userId, Guid conversationId, ChatHistoryMessage message)
    {
        if (string.IsNullOrEmpty(message.Signature)) return (null, "unsigned");
        if (!TryParse(message.Signature, out var parsed)) return (null, "malformed");
        if (!_keys.TryGetValue(parsed.KeyId, out var key)) return (null, "unknown_key");

        var expected = Compute(key, tenantId, userId, conversationId, parsed.Seq, parsed.Previous, message.Role, message.Content);
        return CryptographicOperations.FixedTimeEquals(expected, parsed.MacBytes) ? (parsed, null) : (null, "bad_signature");
    }

    // ----- Format: v1.<keyId>.<seq>.<previous mac, or "-">.<mac> -----

    private readonly record struct Parsed(string KeyId, long Seq, string Previous, string Mac, byte[] MacBytes);

    private static bool TryParse(string? signature, out Parsed parsed)
    {
        parsed = default;
        var parts = signature?.Split('.');
        if (parts is not [Version, var keyId, var seq, var previous, var mac]) return false;
        if (!KeyId().IsMatch(keyId) || !long.TryParse(seq, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
        if (previous != NoPrevious && !IsMac(previous, out _)) return false;
        if (!IsMac(mac, out var macBytes)) return false;

        parsed = new(keyId, number, previous, mac, macBytes);
        return true;
    }

    private static bool IsMac(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length != 43) return false; // 32 bytes, base64url without padding
        try
        {
            bytes = WebEncoders.Base64UrlDecode(value);
        }
        catch (FormatException)
        {
            return false;
        }
        return bytes.Length == 32;
    }

    /// <summary>
    /// HMAC-SHA256 over length-prefixed fields, so no value can be shifted into a neighbouring field. It covers who
    /// (tenant, user), which conversation, where in it (sequence and the previous message), and what was said.
    /// </summary>
    private static byte[] Compute(
        byte[] key, string tenantId, string userId, Guid conversationId, long seq, string previous, string role, string content)
    {
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in new[]
        {
            "helpdesk-history-" + Version, tenantId, userId, conversationId.ToString("N"),
            seq.ToString(CultureInfo.InvariantCulture), previous, role, content
        })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }
        return HMACSHA256.HashData(key, buffer.ToArray());
    }
}
