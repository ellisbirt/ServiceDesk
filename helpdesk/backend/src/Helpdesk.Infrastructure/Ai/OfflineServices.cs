using System.Text.RegularExpressions;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Ai;

/// <summary>
/// Offline stand-ins used only when no Anthropic API key is configured (local development).
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

    public Task<(string Lane, string ResponseContext)> ClassifyAndValidateAsync(
        string userInput, string tenantId, IReadOnlyList<ChatHistoryMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null)
    {
        logger.LogWarning("Using OFFLINE keyword guardrail (no Anthropic API key configured).");

        try
        {
            if (GuardrailPolicyPrefilter.FindPolicyRule(userInput) is not null)
            {
                return Task.FromResult((ActionLanes.PolicyViolation, Prompts.ComplianceFallback));
            }
            if (PrivateRegex().IsMatch(userInput))
            {
                return Task.FromResult((ActionLanes.PrivateCorp, string.Empty));
            }
            if (ExplicitHandoffRequestRegex().IsMatch(userInput))
            {
                return Task.FromResult((ActionLanes.PrivateCorp, string.Empty));
            }
            if (TechnicalFollowUpRegex().IsMatch(userInput)
                && conversationHistory?.Any(message =>
                    message.Role == "user" && PublicTechRegex().IsMatch(message.Content)) == true)
            {
                return Task.FromResult((ActionLanes.PublicTech, string.Empty));
            }
            if (SearchRegex().IsMatch(userInput))
            {
                return Task.FromResult((ActionLanes.PublicSearch, string.Empty));
            }
            if (PublicTechRegex().IsMatch(userInput))
            {
                return Task.FromResult((ActionLanes.PublicTech, string.Empty));
            }
            return Task.FromResult((ActionLanes.NoMatch, string.Empty));
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogWarning("Offline guardrail regex timed out; failing closed as a policy violation.");
            return Task.FromResult((ActionLanes.PolicyViolation, Prompts.ComplianceFallback));
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
        IReadOnlyList<ChatHistoryMessage>? conversationHistory = null, IReadOnlyList<KbHit>? knowledgeBase = null) =>
        ReplyAsync("_Offline mode: no Anthropic API key is configured, so this is a canned reply._\n\nSet `Anthropic:ApiKey` to get real answers."
            + KnowledgeBaseReply.Articles(knowledgeBase), progress, cancellationToken);

    public Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null, IReadOnlyList<KbHit>? knowledgeBase = null) =>
        ReplyAsync($"_Offline mode: canned reply. Search context that would have been used:_\n\n{searchContextMarkdown}"
            + KnowledgeBaseReply.Articles(knowledgeBase), progress, cancellationToken);

    private static async Task<string> ReplyAsync(string text, IChatProgress? progress, CancellationToken ct)
    {
        if (progress is not null) await progress.OnTextAsync(text, ct);
        return text;
    }
}

public sealed partial class OfflineIntakeFormGenerator : IIntakeFormGenerator
{
    [GeneratedRegex(@"\b(access|permission|permissions|grant|membership)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex AccessRequestRegex();

    public Task<IntakeForm> GenerateAsync(
        string userInput, string lane, CancellationToken cancellationToken = default, string? userAgent = null) =>
        Task.FromResult(lane == ActionLanes.PrivateCorp && AccessRequestRegex().IsMatch(userInput)
            ? new IntakeForm
            {
                Title = "Work system access request",
                SubmitLabel = "Send to the service desk",
                Fields =
                [
                    new IntakeFormField
                    {
                        Id = "target_system",
                        Label = "Which system, site or files do you need access to?",
                        Description = "Name the system and any specific site, library or folder.",
                        InputType = "text",
                        Required = true
                    },
                    new IntakeFormField
                    {
                        Id = "access_level",
                        Label = "What level of access do you need?",
                        Description = "Say whether you need to view or edit, and name any specific libraries or folders.",
                        InputType = "textarea",
                        Required = true
                    },
                    new IntakeFormField
                    {
                        Id = "business_reason",
                        Label = "Why do you need this access?",
                        Description = "Describe the work task or business reason.",
                        InputType = "textarea",
                        Required = true
                    },
                    new IntakeFormField
                    {
                        Id = "work_account",
                        Label = "Which work account should receive access?",
                        Description = "Enter your work email or sign-in name only; never include a password or code.",
                        InputType = "text",
                        Required = true
                    },
                    new IntakeFormField
                    {
                        Id = "approval_status",
                        Label = "Has your manager or the site owner approved this?",
                        Description = "If yes, share the approver's name or team; otherwise say approval is still needed.",
                        InputType = "textarea"
                    },
                    new IntakeFormField
                    {
                        Id = "access_end_date",
                        Label = "When should this access end?",
                        Description = "Give an end date, or say if access is only needed through quarterly close.",
                        InputType = "text"
                    }
                ]
            }
            : new IntakeForm
        {
            Title = "Issue details",
            SubmitLabel = "Send to the service desk",
            Fields =
            [
                new IntakeFormField
                {
                    Id = "issue_details",
                    Label = "What is the issue?",
                    Description = "Describe what happened, when it started, and who or what is affected.",
                    InputType = "textarea"
                },
                new IntakeFormField
                {
                    Id = "steps_tried",
                    Label = "What have you tried so far?",
                    Description = "Include any steps that changed the issue.",
                    InputType = "textarea"
                },
                new IntakeFormField
                {
                    Id = "additional_details",
                    Label = "Anything else we should know?",
                    Description = "Add details that could help resolve your request.",
                    InputType = "textarea"
                }
            ]
        });
}
