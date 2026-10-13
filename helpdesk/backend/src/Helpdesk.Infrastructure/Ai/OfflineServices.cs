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
    [GeneratedRegex(@"\b(outages?|is .* down|(?:service|system) status|status page|latest (?:[\w.-]+\s+){0,3}(?:patch(?:es)?|updates?|releases?|known (?:bugs|issues))|patch tuesday|known (bugs|issues))\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex SearchRegex();

    [GeneratedRegex(@"\b(vpn|printer|file share|network share|shared drive|intranet|sharepoint|domain|corporate sso|entra|active directory|(?:work|corporate|company) account|\\\\[a-z0-9_.-]+\\)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateRegex();

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

public sealed class OfflinePublicInferenceService : IPublicInferenceService
{
    public Task<string> ExecutePublicInferenceAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null) =>
        ReplyAsync("_Offline mode: no Anthropic API key is configured, so this is a canned reply._\n\nSet `Anthropic:ApiKey` to get real answers.", progress, cancellationToken);

    public Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null) =>
        ReplyAsync($"_Offline mode: canned reply. Search context that would have been used:_\n\n{searchContextMarkdown}", progress, cancellationToken);

    private static async Task<string> ReplyAsync(string text, IChatProgress? progress, CancellationToken ct)
    {
        if (progress is not null) await progress.OnTextAsync(text, ct);
        return text;
    }
}

public sealed class OfflineIntakeFormGenerator : IIntakeFormGenerator
{
    public Task<IntakeForm> GenerateAsync(
        string userInput, string lane, CancellationToken cancellationToken = default, string? userAgent = null) =>
        Task.FromResult(new IntakeForm
        {
            Title = "Tell us more about your issue",
            SubmitLabel = "Save details",
            Fields =
            [
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
