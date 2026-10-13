using System.Text.RegularExpressions;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Security;

/// <summary>
/// Pattern-based data loss prevention for text leaving the service. Best effort, not a DLP product.
/// <list type="bullet">
/// <item>Always removed: secrets (private keys, JWTs, bearer tokens, common API-key formats, URL credentials,
/// common natural-language credential disclosures, and
/// <c>password=</c>-style assignments) and Luhn-valid card numbers.</item>
/// <item>Removed unless the policy allows it for the destination and tenant (see <see cref="DlpPolicy"/>):
/// government ids (US SSN, UK National Insurance number), IBANs, email addresses and phone numbers.</item>
/// </list>
/// Not caught: names, addresses, and personal data without a recognisable format.
/// </summary>
public sealed partial class SensitiveDataRedactor(IOptions<DlpOptions> options) : ISensitiveDataRedactor
{
    private const int TimeoutMs = 250;
    private readonly DlpPolicy _policy = new(options.Value);

    /// <summary>Returns the replacement for a match, or null to leave it (it matched the shape but failed validation).</summary>
    private delegate string? Evaluate(Match match, string marker);

    private sealed record Detector(DlpCategory Category, Regex Pattern, Evaluate Replace);

    private static string? Always(Match m, string marker) => marker;

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\b(?:sk-ant-[A-Za-z0-9_-]{20,}|sk-[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,})",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex KnownTokenFormats();

    // user:password@ in a URL or connection string.
    [GeneratedRegex(@"(?<=://)[^/\s:@]+:[^/\s@]+(?=@)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UrlCredentials();

    // password=..., "client_secret": "...", AccountKey=...; the key name is kept so the text still makes sense.
    [GeneratedRegex(
        @"(?<key>\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|account[_-]?key|shared[_-]?access[_-]?key|client[_-]?secret)[""']?\s*[:=]\s*)(?<value>""[^""\r\n]+""|'[^'\r\n]+'|[^\s;,""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(
        @"(?<key>\b(?:(?:my|the|our|his|her|their)\s+)?(?:shared[_ -]?access[_ -]?key|client[_ -]?secret|access[_ -]?key|account[_ -]?key|api[_ -]?key|password|passwd|pwd|secret|token)\s+(?:is|was|equals)\s*)(?<value>""[^""\r\n]+""|'[^'\r\n]+'|(?!incorrect\b|wrong\b|invalid\b|expired\b|not\b|working\b|available\b|configured\b)[^\s;,.!?]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex NaturalLanguageSecret();

    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CardCandidate();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex IbanCandidate();

    // US SSN, excluding the ranges that are never issued (000, 666, 9xx; group 00; serial 0000).
    [GeneratedRegex(@"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UsSsn();

    [GeneratedRegex(@"\b[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z] ?\d{2} ?\d{2} ?\d{2} ?[A-D]\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UkNationalInsurance();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex EmailAddress();

    // International numbers need a leading +; national ones are only matched in the common US layout, so version
    // numbers, build numbers and IP addresses are left alone.
    [GeneratedRegex(@"(?<![\w+])\+\d{1,3}[ .-]?\(?\d{1,4}\)?(?:[ .-]?\d{2,4}){2,4}(?!\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex InternationalPhone();

    [GeneratedRegex(@"(?<![\w.-])\(?\d{3}\)?[ .-]\d{3}[ .-]\d{4}(?![\w-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UsPhone();

    // Order matters: secrets first, then the numeric formats from most to least specific.
    private static readonly Detector[] Detectors =
    [
        new(DlpCategory.Secret, PrivateKeyBlock(), Always),
        new(DlpCategory.Secret, Jwt(), Always),
        new(DlpCategory.Secret, BearerToken(), (_, marker) => "Bearer " + marker),
        new(DlpCategory.Secret, KnownTokenFormats(), Always),
        new(DlpCategory.Secret, UrlCredentials(), Always),
        new(DlpCategory.Secret, SecretAssignment(), (m, marker) => m.Groups["key"].Value + marker),
        new(DlpCategory.Secret, NaturalLanguageSecret(), (m, marker) => m.Groups["key"].Value + marker),
        new(DlpCategory.Card, CardCandidate(), (m, marker) => IsCardNumber(m.Value) ? marker : null),
        new(DlpCategory.Iban, IbanCandidate(), (m, marker) => IsIban(m.Value) ? marker : null),
        new(DlpCategory.GovernmentId, UsSsn(), Always),
        new(DlpCategory.GovernmentId, UkNationalInsurance(), Always),
        new(DlpCategory.Email, EmailAddress(), Always),
        new(DlpCategory.Phone, InternationalPhone(), Always),
        new(DlpCategory.Phone, UsPhone(), Always),
    ];

    private static string Marker(DlpCategory category) => category switch
    {
        DlpCategory.GovernmentId => "[REDACTED:id]",
        _ => $"[REDACTED:{category.ToString().ToLowerInvariant()}]"
    };

    public RedactionResult Redact(string text, EgressDestination destination = EgressDestination.Model, string? tenantId = null)
    {
        if (string.IsNullOrEmpty(text)) return new(text, 0);

        var counts = new Dictionary<string, int>();
        try
        {
            foreach (var detector in Detectors)
            {
                if (_policy.Resolve(detector.Category, destination, tenantId) == DlpAction.Allow) continue;

                var marker = Marker(detector.Category);
                text = detector.Pattern.Replace(text, match =>
                {
                    var replacement = detector.Replace(match, marker);
                    if (replacement is null) return match.Value;
                    var name = detector.Category.ToString();
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                    return replacement;
                });
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: if we cannot prove the text is clean, nothing of it leaves the service.
            Record("Unscannable", destination, 1);
            return new("[REDACTED:unscannable]", 1, new Dictionary<string, int> { ["Unscannable"] = 1 });
        }

        foreach (var (category, n) in counts) Record(category, destination, n);
        return new(text, counts.Values.Sum(), counts);
    }

    private static void Record(string category, EgressDestination destination, int count) =>
        Telemetry.Redactions.Add(count,
            new KeyValuePair<string, object?>("category", category),
            new KeyValuePair<string, object?>("destination", destination.ToString()));

    private static bool IsCardNumber(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).ToArray();
        if (digits.Length is < 13 or > 19) return false;

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[^(i + 1)] - '0';
            if (i % 2 == 1 && (d *= 2) > 9) d -= 9;
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>ISO 13616 check: move the first four characters to the end, letters become 10-35, remainder mod 97 must be 1.</summary>
    private static bool IsIban(string candidate)
    {
        var iban = candidate.Replace(" ", "");
        if (iban.Length is < 15 or > 34) return false;

        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            var value = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = (value > 9 ? remainder * 100 + value : remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }
}
