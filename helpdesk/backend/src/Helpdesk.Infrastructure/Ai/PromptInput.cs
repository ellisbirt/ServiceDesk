using Helpdesk.Core.Interfaces;

namespace Helpdesk.Infrastructure.Ai;

internal static class PromptInput
{
    // The unpredictable boundary token prevents untrusted content from forging its enclosing delimiters.
    public static string Wrap(string tag, string text)
    {
        var boundary = Guid.NewGuid().ToString("N");
        return $"[BEGIN UNTRUSTED {tag} {boundary}]\n{text}\n[END UNTRUSTED {tag} {boundary}]";
    }

    public static string WithUserAgent(string content, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return content;

        var safeUserAgent = new string(userAgent
            .Where(character => !char.IsControl(character))
            .Take(512)
            .ToArray());
        return $"{content}\n\n{Wrap("client_context", $"User-Agent: {safeUserAgent}")}";
    }

    /// <summary>Each retrieved article is wrapped separately: its content was written by people and is data, not instructions.</summary>
    public static string KnowledgeBase(IReadOnlyList<KbHit>? hits) =>
        hits is not { Count: > 0 }
            ? ""
            : string.Join("\n", hits.Select(h => Wrap("kb_article", $"{h.Number}: {h.Title}\n{h.Excerpt}"))) + "\n\n";
}
