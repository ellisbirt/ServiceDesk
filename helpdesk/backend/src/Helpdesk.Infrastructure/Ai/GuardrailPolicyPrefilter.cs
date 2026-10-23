using System.Text;
using System.Text.RegularExpressions;
using Helpdesk.Shared.Contracts;

namespace Helpdesk.Infrastructure.Ai;

internal static partial class GuardrailPolicyPrefilter
{
    private const int MaximumInputLength = 16_384;

    [GeneratedRegex(
        @"\b(?:give|send|share|tell|provide|find|look\s+up|retrieve|obtain|get|need|use|access|reveal|reset|change|what(?:'s| is))\b.{0,100}\b(?:password|passcode|credentials?|login details|access token|secret)\b.{0,100}\b(?:for|of|belonging to)\b.{0,80}\b(?:someone else|another (?:user|person|employee)|(?:my )?(?:coworker|colleague|manager|boss|supervisor|teammate)|(?:on|while) leave)\b|\b(?:someone else|another (?:user|person|employee)|(?:my )?(?:coworker|colleague|manager|boss|supervisor|teammate))['’]s?\s+(?:password|passcode|credentials?|login details|access token|secret)\b.{0,80}\b(?:give|send|share|tell|provide|find|look\s+up|get|need|use|access|reveal|reset|change)\b|\b[a-z][a-z0-9_-]{1,30}['’]s\s+(?:password|passcode|credentials?|login details|access token|secret)\b.{0,80}\b(?:give|send|share|tell|provide|find|look\s+up|get|need|use|access|reveal|reset|change)\b|\b(?:give|send|share|tell|provide|find|look\s+up|retrieve|obtain|get|need|use|access|reveal|reset|change)\b.{0,100}\b[a-z][a-z0-9_-]{1,30}['’]s\s+(?:password|passcode|credentials?|login details|access token|secret)\b|\b(?:give|send|share|tell|provide|find|look\s+up|retrieve|obtain|get|need|use|access|reveal|reset|change|what(?:'s| is))\b.{0,80}\b[a-z][a-z0-9_-]{1,30}['’]s\s+(?:password|passcode|credentials?|login details|access token|secret)\b|\b(?:reset|change|send|share|give|tell|provide|retrieve|get|reveal|obtain|need|use)\b.{0,80}\b(?:his|her|their)\s+(?:password|passcode|credentials?|login details|access token|secret)\b|\b(?:his|her|their)\s+(?:password|passcode|credentials?|login details|access token|secret)\b.{0,80}\b(?:reset|change|send|share|give|tell|provide|retrieve|get|reveal|obtain|need|use)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ThirdPartyCredentialRequest();

    [GeneratedRegex(
        @"\b(?:bypass|turn\s+off|disable|deactivate|circumvent|evade|get\s+around|work\s+around|override)\b.{0,80}\b(?:mfa|2fa|multi[- ]?factor|authentication|security check|firewall|network restriction|access control)\b|\b(?:mfa|2fa|multi[- ]?factor|authentication|security check|firewall|network restriction|access control)\b.{0,80}\b(?:bypass|turn\s+off|disable|deactivate|circumvent|evade|get\s+around|work\s+around|override)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecurityControlEvasion();

    [GeneratedRegex(
        @"\b(?:pretend\s+to\s+be|impersonat\w*|pose\s+as|masquerad\w*)\b.{0,100}\b(?:password|credentials?|login|account|mfa|2fa|access|security)\b|\b(?:password|credentials?|login|account|mfa|2fa|access|security)\b.{0,100}\b(?:pretend\s+to\s+be|impersonat\w*|pose\s+as|masquerad\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ImpersonationForAccess();

    [GeneratedRegex(
        @"^\s*(?:how\s+(?:do|can)\s+i\s+)?skip\s+authentication\s+in\s+my\s+unit tests?\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex StandaloneUnitTestQuestion();

    public static string? FindPolicyRule(string userInput)
    {
        if (userInput.Length > MaximumInputLength) return "input_too_long";

        try
        {
            var normalized = RemoveInvisibleCharacters(userInput.Normalize(NormalizationForm.FormKC));
            if (ThirdPartyCredentialRequest().IsMatch(normalized)) return "third_party_credential_request";
            if (SecurityControlEvasion().IsMatch(normalized) && !StandaloneUnitTestQuestion().IsMatch(normalized))
                return "security_control_evasion";
            if (ImpersonationForAccess().IsMatch(normalized)) return "impersonation_for_access";
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return "regex_timeout";
        }
    }

    private static string RemoveInvisibleCharacters(string text) =>
        string.Concat(text.Select(character =>
        {
            var category = char.GetUnicodeCategory(character);
            return category == System.Globalization.UnicodeCategory.Format
                ? string.Empty
                : category == System.Globalization.UnicodeCategory.Control ? " " : character.ToString();
        }));
}
