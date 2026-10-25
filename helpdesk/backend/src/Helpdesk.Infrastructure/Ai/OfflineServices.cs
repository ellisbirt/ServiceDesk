using System.Text.RegularExpressions;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Helpdesk.Shared.Events;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Ai;

/// <summary>
/// Offline stand-ins used only when no chat provider is configured (local development).
/// The keyword router is intentionally coarse and biased towards the restrictive lanes.
/// </summary>
public sealed partial class OfflineGuardrailRouterService(ILogger<OfflineGuardrailRouterService> logger) : IGuardrailRouterService
{
    [GeneratedRegex(@"\b(outages?|is .* down|(?:service|system) status|status page|latest (?:[\w.-]+\s+){0,3}(?:patch(?:es)?|update(?:s?\s+(?:for|on|about)\b)|releases?|known (?:bugs|issues))|patch tuesday|known (bugs|issues))\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex SearchRegex();

    [GeneratedRegex(@"\b(vpn|printer|file share|network share|shared drive|intranet|sharepoint|domain|corporate sso|entra|active directory|(?:work|corporate|company) account|\\\\[a-z0-9_.-]+\\)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateRegex();

    [GeneratedRegex(@"\b(open|raise|create|log|file|submit)\s+(?:a\s+)?(?:(?:service[- ]desk|support)\s+)?(?:ticket|request)\b|\b(speak|talk|connect)\s+to\s+(?:a\s+)?(?:person|human|agent|support (?:person|team))\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex ExplicitHandoffRequestRegex();

    [GeneratedRegex(@"\b(?:it|that|this|the issue|the problem|that step|those steps)\s+(?:is\s+)?(?:still\s+)?(?:(?:isn't|is not|not|won't|will not|doesn't|does not|can't|cannot)\s+(?:working|fixed|resolved|connecting|loading|work|connect|load|respond)|broken|unresolved|didn't work|did not work|hasn't helped|has not helped|worked|works now|is fixed|has been fixed|is resolved|is working now)|\b(?:it|that|this)(?:'s|\s+is)\s+(?:still\s+)?(?:not working|not fixed|not resolved|broken|unresolved|fixed|resolved|working now)|\b(?:still happening|still broken|no luck|didn't work|did not work|hasn't helped|has not helped|works fine now|all sorted|sorted now|resolved now|that fixed it|no[, ]+(?:it|that|same)|same (?:issue|problem|thing)|no change|nothing changed|not yet|nope)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex TechnicalFollowUpRegex();

    [GeneratedRegex(@"\b(code|coding|programming|script|python|javascript|typescript|linux|windows|macos|outlook|excel|word|powerpoint|operating system|software|application|install|configure|configuration|compile|debug|error message|stack trace|api|database|sql|git|terminal|command line|dotnet|laptop|desktop|computer|phone|tablet|battery|charging|charger|overheating|screen|monitor|display|audio|microphone|speakers?|headphones?|webcam|keyboard|mouse|touchpad|wi[- ]?fi|internet|bluetooth|browser|email|teams|zoom|disk|storage|log\s*in|sign[- ]?in|mfa|2fa|phishing|malware|antivirus|firewall)\b|\b(?:my (?:own )?(?:account|password)|(?:incorrect|forgotten|reset|recover|forgot) password|password (?:is )?(?:incorrect|wrong|reset|recovery)|account (?:locked|lockout|recovery))\b|c\#(?=\W|$)|\.net\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex PublicTechRegex();

    public Task<string> CoachAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null) =>
        Task.FromResult(Prompts.ComplianceFallback);

    public Task<Classification> ClassifyAndValidateAsync(
        string userInput, string tenantId, IReadOnlyList<ChatHistoryMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null)
    {
        logger.LogWarning("Using OFFLINE keyword guardrail (no chat provider configured).");

        try
        {
            if (GuardrailPolicyPrefilter.FindPolicyRule(userInput) is not null)
            {
                return Task.FromResult(new Classification(ActionLanes.PolicyViolation, Prompts.ComplianceFallback));
            }
            if (PrivateRegex().IsMatch(userInput))
            {
                return Task.FromResult(new Classification(ActionLanes.PrivateCorp, string.Empty));
            }
            if (ExplicitHandoffRequestRegex().IsMatch(userInput))
            {
                return Task.FromResult(new Classification(ActionLanes.PrivateCorp, string.Empty));
            }
            if (TechnicalFollowUpRegex().IsMatch(userInput)
                && conversationHistory?.Any(message =>
                    message.Role == "user" && PublicTechRegex().IsMatch(message.Content)) == true)
            {
                return Task.FromResult(new Classification(ActionLanes.PublicTech, string.Empty));
            }
            if (SearchRegex().IsMatch(userInput))
            {
                return Task.FromResult(new Classification(ActionLanes.PublicSearch, string.Empty));
            }
            if (PublicTechRegex().IsMatch(userInput))
            {
                return Task.FromResult(new Classification(ActionLanes.PublicTech, string.Empty));
            }
            return Task.FromResult(new Classification(ActionLanes.NoMatch, string.Empty));
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogWarning("Offline guardrail regex timed out; failing closed as a policy violation.");
            return Task.FromResult(new Classification(ActionLanes.PolicyViolation, Prompts.ComplianceFallback));
        }
    }
}

/// <summary>Plain Markdown listing of retrieved KB articles, for replies that are not written by a model.</summary>
public static class KnowledgeBaseReply
{
    public static string Articles(IReadOnlyList<KbHit>? hits) =>
        hits is not { Count: > 0 }
            ? ""
            : "\n\nThese knowledge-base articles may help:\n\n"
              + string.Join("\n", hits.Select(h => $"- **{h.Number}**: {h.Title}" + (h.Summary.Length > 0 ? $" ({h.Summary})" : "")));
}

public sealed class OfflinePublicInferenceService : IPublicInferenceService
{
    public Task<string> ExecutePublicInferenceAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null,
        IReadOnlyList<ChatHistoryMessage>? conversationHistory = null, IReadOnlyList<KbHit>? knowledgeBase = null,
        Helpdesk.Llm.ModelTier tier = Helpdesk.Llm.ModelTier.Standard, AttachmentContext? attachments = null) =>
        ReplyAsync("_Offline mode: no chat provider is configured, so this is a canned reply._\n\nConfigure a chat provider (`Llm:Roles:chat`, or `Anthropic:ApiKey`) to get real answers."
            + KnowledgeBaseReply.Articles(knowledgeBase), progress, cancellationToken);

    public Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null, IReadOnlyList<KbHit>? knowledgeBase = null,
        Helpdesk.Llm.ModelTier tier = Helpdesk.Llm.ModelTier.Standard, AttachmentContext? attachments = null) =>
        ReplyAsync($"_Offline mode: canned reply. Search context that would have been used:_\n\n{searchContextMarkdown}"
            + KnowledgeBaseReply.Articles(knowledgeBase), progress, cancellationToken);

    private static async Task<string> ReplyAsync(string text, IChatProgress? progress, CancellationToken ct)
    {
        if (progress is not null) await progress.OnTextAsync(text, ct);
        return text;
    }
}

/// <summary>
/// Offline stand-in: asks one question, drafts the ticket on the next user message, and queues nothing until the user
/// answers the draft with a yes. Real behaviour needs a model.
/// </summary>
public sealed partial class OfflineTicketInterviewer : ITicketInterviewer
{
    [GeneratedRegex(@"^\s*(yes|y|yep|yeah|ok|okay|sure|send( it)?|confirm|go ahead)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Agreement();

    [GeneratedRegex(@"^\s*(no|nope|cancel|don't|do not|stop)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Refusal();

    public Task<TicketInterview> InterviewAsync(
        string conversation, CancellationToken cancellationToken = default, string? userAgent = null, bool mustRaise = false,
        bool proposalPending = false)
    {
        var userMessages = conversation.Split('\n')
            .Where(l => l.StartsWith("user:", StringComparison.Ordinal))
            .Select(l => l["user:".Length..].Trim())
            .ToList();

        if (proposalPending)
        {
            var answer = userMessages[^1];
            userMessages.RemoveAt(userMessages.Count - 1);
            if (Refusal().IsMatch(answer))
            {
                return Task.FromResult(new TicketInterview(
                    false, "_Offline mode._ Okay, I haven't sent anything.", null, null, null, null, null));
            }
            var agreed = Agreement().IsMatch(answer);
            if (!agreed) userMessages.Add(answer);
            return Task.FromResult(Draft(agreed ? "_Offline mode._ I've passed this to the service desk." : "_Offline mode._ I've updated it.",
                userMessages, agreed));
        }

        if (userMessages.Count < 2 && !mustRaise)
        {
            return Task.FromResult(new TicketInterview(
                false, "_Offline mode._ Could you tell me a little more about what you need and when it started?", null, null, null, null, null));
        }

        return Task.FromResult(Draft("_Offline mode._ Here's what I'd send.", userMessages, userConfirmed: false));
    }

    private static TicketInterview Draft(string message, List<string> userMessages, bool userConfirmed) => new(
        true, message, TicketSubject.FromText(userMessages[0]), userMessages[^1],
        string.Join("\n", userMessages.Select(m => $"- {m}")), null, null, userConfirmed);
}
