
namespace Helpdesk.Dlp;

/// <summary>
/// Overrides for what is redacted where. Only overrides go in configuration; the defaults live in
/// <see cref="DlpPolicy"/>. Names are case-insensitive: destinations are <c>Model</c>, <c>Search</c>, <c>Queue</c>, <c>Embedding</c>;
/// categories are <c>GovernmentId</c>, <c>Iban</c>, <c>Email</c>, <c>Phone</c>; actions are <c>Redact</c>, <c>Allow</c>.
/// <code>
/// "Dlp": {
///   "Destinations": { "Model": { "Email": "Redact" } },
///   "Tenants": { "contoso-guid": { "Queue": { "Phone": "Redact" } } }
/// }
/// </code>
/// </summary>
public sealed class DlpOptions
{
    public const string SectionName = "Dlp";

    /// <summary>Destination to category to action.</summary>
    public Dictionary<string, Dictionary<string, string>> Destinations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tenant id to destination to category to action. A tenant entry wins over <see cref="Destinations"/>.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Tenants { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The resolved, validated form of <see cref="DlpOptions"/>.</summary>
public sealed class DlpPolicy
{
    private readonly Dictionary<(EgressDestination, DlpCategory), DlpAction> _global = [];
    private readonly Dictionary<string, Dictionary<(EgressDestination, DlpCategory), DlpAction>> _tenants = new(StringComparer.Ordinal);

    /// <summary>Problems found in the options (unknown names, attempts to allow a mandatory category). Never contains values from user text.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Secrets and card numbers cannot be allowed anywhere.</summary>
    public static bool IsMandatory(DlpCategory category) => category is DlpCategory.Secret or DlpCategory.Card;

    public static DlpAction Default(DlpCategory category, EgressDestination destination) => category switch
    {
        // Work email addresses and phone numbers are often what an IT problem is about, so the model and the
        // ticket keep them; a third-party search engine and stored KB vectors never need them.
        DlpCategory.Email or DlpCategory.Phone => destination is EgressDestination.Search or EgressDestination.Embedding ? DlpAction.Redact : DlpAction.Allow,
        _ => DlpAction.Redact
    };

    public DlpPolicy(DlpOptions options)
    {
        var errors = new List<string>();
        Load(options.Destinations, _global, "Dlp:Destinations", errors);
        foreach (var (tenant, destinations) in options.Tenants)
        {
            var map = new Dictionary<(EgressDestination, DlpCategory), DlpAction>();
            Load(destinations, map, $"Dlp:Tenants:{tenant}", errors);
            _tenants[tenant] = map;
        }
        Errors = errors;
    }

    public DlpAction Resolve(DlpCategory category, EgressDestination destination, string? tenantId)
    {
        if (IsMandatory(category)) return DlpAction.Redact;

        var key = (destination, category);
        if (tenantId is not null && _tenants.TryGetValue(tenantId, out var tenant) && tenant.TryGetValue(key, out var fromTenant))
        {
            return fromTenant;
        }
        return _global.TryGetValue(key, out var fromGlobal) ? fromGlobal : Default(category, destination);
    }

    private static void Load(
        Dictionary<string, Dictionary<string, string>> source,
        Dictionary<(EgressDestination, DlpCategory), DlpAction> target, string path, List<string> errors)
    {
        foreach (var (destinationName, categories) in source)
        {
            if (!Enum.TryParse<EgressDestination>(destinationName, ignoreCase: true, out var destination) || !Enum.IsDefined(destination))
            {
                errors.Add($"{path}:{destinationName} is not a destination (Model, Search, Queue, Embedding).");
                continue;
            }
            foreach (var (categoryName, actionName) in categories)
            {
                if (!Enum.TryParse<DlpCategory>(categoryName, ignoreCase: true, out var category) || !Enum.IsDefined(category))
                {
                    errors.Add($"{path}:{destinationName}:{categoryName} is not a category (GovernmentId, Iban, Email, Phone; Secret and Card are fixed).");
                    continue;
                }
                if (!Enum.TryParse<DlpAction>(actionName, ignoreCase: true, out var action) || !Enum.IsDefined(action))
                {
                    errors.Add($"{path}:{destinationName}:{categoryName} must be Redact or Allow.");
                    continue;
                }
                if (IsMandatory(category) && action == DlpAction.Allow)
                {
                    errors.Add($"{path}:{destinationName}:{categoryName} cannot be Allow; secrets and card numbers are always redacted.");
                    continue;
                }
                target[(destination, category)] = action;
            }
        }
    }
}
