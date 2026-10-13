using System.Text.RegularExpressions;
using Helpdesk.Api.Chat;
using Helpdesk.Api.Hosting;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Messaging;
using Helpdesk.Api.Security;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Infrastructure.Security;

namespace Helpdesk.Api.Status;

public static class CheckStatus
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

public sealed record ConfigCheck(string Area, string Name, string Status, string Message);

/// <param name="Status"><c>ok</c> (all pass), <c>degraded</c> (warnings only) or <c>invalid</c> (at least one failure).</param>
public sealed record StatusReport(string Status, string Environment, IReadOnlyList<ConfigCheck> Checks);

/// <summary>
/// Validates the effective configuration without calling any external service. Messages name settings and
/// say what is wrong; they never include secret values (keys, connection strings), so the report is safe to read
/// but is still restricted to operators because it describes the deployment.
/// </summary>
public sealed partial class ConfigurationValidator(IConfiguration config, IHostEnvironment env)
{
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]$")]
    private static partial Regex StorageQueueName();

    public StatusReport Validate()
    {
        var checks = new List<ConfigCheck>();
        void Add(string area, string name, string status, string message) => checks.Add(new(area, name, status, message));
        void Pass(string area, string name, string message) => Add(area, name, CheckStatus.Pass, message);
        void Warn(string area, string name, string message) => Add(area, name, CheckStatus.Warn, message);
        void Fail(string area, string name, string message) => Add(area, name, CheckStatus.Fail, message);
        void Check(bool ok, string area, string name, string okMessage, string failMessage, bool warnOnly = false)
        {
            if (ok) Pass(area, name, okMessage);
            else Add(area, name, warnOnly ? CheckStatus.Warn : CheckStatus.Fail, failMessage);
        }

        var dev = env.IsDevelopment();
        ValidateAuth(dev, Pass, Warn, Fail, Check);
        ValidateAnthropic(dev, Pass, Warn, Fail, Check);
        ValidateSearch(Pass, Warn, Fail, Check);
        ValidateQueue(dev, Pass, Warn, Fail, Check);
        ValidateDlp(Pass, Fail);
        ValidateChat(Check);
        ValidateHub(Check);
        ValidateConversation(dev, Pass, Warn, Fail);
        ValidateWeb(dev, Pass, Warn, Fail, Check);
        ValidateHttp(dev, Pass, Warn, Fail, Check);

        var status = checks.Any(c => c.Status == CheckStatus.Fail) ? "invalid"
            : checks.Any(c => c.Status == CheckStatus.Warn) ? "degraded"
            : "ok";
        return new StatusReport(status, env.EnvironmentName, checks);
    }

    private delegate void Report(string area, string name, string message);
    private delegate void Verify(bool ok, string area, string name, string okMessage, string failMessage, bool warnOnly = false);

    private void ValidateAuth(bool dev, Report pass, Report warn, Report fail, Verify check)
    {
        const string area = "Auth";
        var devAuthRequested = config.GetValue<bool>("Auth:UseDevAuth");

        if (dev && devAuthRequested)
        {
            warn(area, "Mode", "Development header authentication is active. Never use this outside local development.");
            return;
        }
        if (devAuthRequested)
        {
            warn(area, "UseDevAuth", "Auth:UseDevAuth is true but ignored because the environment is not Development; set it to false.");
        }

        pass(area, "Mode", "JWT bearer authentication.");
        var authority = config["Auth:Authority"];
        check(Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
            area, "Authority", "Auth:Authority is an https URL.",
            "Auth:Authority must be an absolute https URL (e.g. https://<tenant>.ciamlogin.com/<tenant-id>/v2.0); every token will be rejected.");
        check(!string.IsNullOrWhiteSpace(config["Auth:Audience"]), area, "Audience",
            "Auth:Audience is set.", "Auth:Audience is empty; every token will be rejected.");
    }

    private void ValidateAnthropic(bool dev, Report pass, Report warn, Report fail, Verify check)
    {
        const string area = "Anthropic";
        var o = config.GetSection(AnthropicOptions.SectionName).Get<AnthropicOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = config["ANTHROPIC_API_KEY"] ?? "";

        if (o.IsConfigured) pass(area, "ApiKey", "An API key is configured.");
        else if (dev) warn(area, "ApiKey", "No API key: offline keyword router and canned replies are used (Development only).");
        else fail(area, "ApiKey", "Anthropic:ApiKey (or ANTHROPIC_API_KEY) is required outside Development.");

        check(!string.IsNullOrWhiteSpace(o.Model), area, "Model", $"Model is '{o.Model}'.", "Anthropic:Model is empty.");
        check(Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var baseUrl) && baseUrl.Scheme == Uri.UriSchemeHttps,
            area, "BaseUrl", "Anthropic:BaseUrl is an https URL.", "Anthropic:BaseUrl must be an absolute https URL.");
        check(!string.IsNullOrWhiteSpace(o.AnthropicVersion), area, "Version", "anthropic-version is set.", "Anthropic:AnthropicVersion is empty.");

        check(o.RouterMaxTokens > 0 && o.InferenceMaxTokens > 0, area, "MaxTokens",
            "Token limits are positive.", "Anthropic:RouterMaxTokens and Anthropic:InferenceMaxTokens must be greater than 0.");
        check(o.CoachTemperature is >= 0 and <= 1 && o.InferenceTemperature is >= 0 and <= 1, area, "Temperature",
            "Temperatures are within 0 to 1.", "Anthropic:CoachTemperature and Anthropic:InferenceTemperature must be between 0 and 1.");
        check(o.RateLimitMaxWaitSeconds >= 0 && o.RateLimitMaxRetries >= 0, area, "RateLimits",
            "Rate-limit wait and retries are not negative.",
            "Anthropic:RateLimitMaxWaitSeconds and Anthropic:RateLimitMaxRetries must not be negative.");
        check(o.StreamIdleTimeoutSeconds > 0, area, "StreamIdleTimeout",
            "Stream idle timeout is positive.", "Anthropic:StreamIdleTimeoutSeconds must be greater than 0.");
        check(o.WebSearchMaxUses > 0 && !string.IsNullOrWhiteSpace(o.WebSearchToolType), area, "WebSearchTool",
            "Web search tool settings are valid.", "Anthropic:WebSearchToolType must be set and Anthropic:WebSearchMaxUses greater than 0.");
    }

    private void ValidateSearch(Report pass, Report warn, Report fail, Verify check)
    {
        const string area = "Search";
        var search = config.GetSection(SearchOptions.SectionName).Get<SearchOptions>() ?? new();
        check(search.TopResults >= 1, area, "TopResults", "Search:TopResults is at least 1.", "Search:TopResults must be at least 1.");

        if (SearchProviderCatalog.IsNativeClaude(search.Provider))
        {
            pass(area, "Provider", "Claude native web search (no separate credentials; web search must be enabled for the Anthropic organisation).");
            return;
        }

        var definition = SearchProviderCatalog.Find(search.Provider);
        if (definition is null)
        {
            fail(area, "Provider", $"Unknown Search:Provider '{search.Provider}'. Available: {string.Join(", ", SearchProviderCatalog.Names)}.");
            return;
        }

        var options = config.GetSection($"{SearchOptions.SectionName}:Providers:{definition.Name}").Get<SearchProviderOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.ApiKey)) options.ApiKey = search.ApiKey;

        if (!definition.IsConfigured(options))
        {
            warn(area, "Provider", $"{definition.Name} has no credentials or endpoint; a stub is used and search answers say no results are available.");
            return;
        }

        pass(area, "Provider", $"{definition.Name} is configured.");
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            check(Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https",
                area, "BaseUrl", $"{definition.Name} BaseUrl is a valid URL.", $"Search:Providers:{definition.Name}:BaseUrl must be an absolute http(s) URL.");
        }
    }

    private void ValidateQueue(bool dev, Report pass, Report warn, Report fail, Verify check)
    {
        const string area = "Queue";
        var provider = config.GetSection(QueueOptions.SectionName).Get<QueueOptions>()?.Provider;
        if (string.IsNullOrWhiteSpace(provider))
        {
            if (!dev)
            {
                fail(area, "Provider", $"Queue:Provider must be '{QueueProviders.StorageQueue}' or '{QueueProviders.ServiceBus}' outside Development.");
                return;
            }
            provider = QueueProviders.StorageQueue;
            warn(area, "Provider", "Queue:Provider is not set; defaulting to StorageQueue (Development only).");
        }

        switch (provider)
        {
            case QueueProviders.StorageQueue:
            {
                pass(area, "Provider", "StorageQueue.");
                var o = config.GetSection(StorageQueueOptions.SectionName).Get<StorageQueueOptions>() ?? new();
                var hasConnection = !string.IsNullOrWhiteSpace(o.ConnectionString);
                check(hasConnection || Uri.TryCreate(o.QueueServiceUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                    area, "Connection", hasConnection ? "A connection string is configured." : "StorageQueue:QueueServiceUri is an https URL (managed identity).",
                    "Set StorageQueue:ConnectionString, or StorageQueue:QueueServiceUri to an https URL for managed identity.");
                if (hasConnection && !dev)
                {
                    warn(area, "Credential", "A storage connection string (shared key) is used; prefer QueueServiceUri with a managed identity.");
                }
                check(StorageQueueName().IsMatch(o.QueueName ?? ""), area, "QueueName",
                    $"Queue name '{o.QueueName}' is valid.",
                    "StorageQueue:QueueName must be 3-63 characters: lowercase letters, digits and single hyphens, starting and ending with a letter or digit.");
                break;
            }
            case QueueProviders.ServiceBus:
            {
                pass(area, "Provider", "ServiceBus.");
                var o = config.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new();
                var hasConnection = !string.IsNullOrWhiteSpace(o.ConnectionString);
                check(hasConnection || !string.IsNullOrWhiteSpace(o.FullyQualifiedNamespace), area, "Connection",
                    hasConnection ? "A connection string is configured." : "ServiceBus:FullyQualifiedNamespace is set (managed identity).",
                    "Set ServiceBus:ConnectionString, or ServiceBus:FullyQualifiedNamespace for managed identity.");
                if (hasConnection && !dev)
                {
                    warn(area, "Credential", "A Service Bus connection string is used; prefer FullyQualifiedNamespace with a managed identity.");
                }
                check(!string.IsNullOrWhiteSpace(o.QueueName), area, "QueueName", $"Queue name '{o.QueueName}' is set.", "ServiceBus:QueueName is empty.");
                break;
            }
            default:
                fail(area, "Provider", $"Unknown Queue:Provider '{provider}'. Use '{QueueProviders.StorageQueue}' or '{QueueProviders.ServiceBus}'.");
                break;
        }
    }

    private void ValidateDlp(Report pass, Report fail)
    {
        const string area = "Dlp";
        var policy = new DlpPolicy(config.GetSection(DlpOptions.SectionName).Get<DlpOptions>() ?? new());
        if (policy.Errors.Count == 0)
        {
            pass(area, "Policy", "Data loss prevention policy is valid.");
            return;
        }
        foreach (var error in policy.Errors) fail(area, "Policy", error);
    }

    private void ValidateConversation(bool dev, Report pass, Report warn, Report fail)
    {
        const string area = "Conversation";
        var o = config.GetSection(ConversationSigningOptions.SectionName).Get<ConversationSigningOptions>() ?? new();

        if (o.Keys.Count == 0)
        {
            if (dev) warn(area, "Keys", "No signing key: a temporary one is used, so conversation history does not survive an API restart (Development only).");
            else fail(area, "Keys", "Conversation:Keys must contain at least one base64 signing key of 32 bytes or more; history cannot be trusted without it.");
            return;
        }

        var errors = o.Validate();
        if (errors.Count == 0)
        {
            pass(area, "Keys", $"{o.Keys.Count} signing key(s) configured; signing with '{(string.IsNullOrEmpty(o.ActiveKeyId) ? o.Keys.Keys.Single() : o.ActiveKeyId)}'.");
            return;
        }
        foreach (var error in errors) fail(area, "Keys", error);
    }

    private void ValidateHub(Verify check)
    {
        const string area = "Hub";
        var o = config.GetSection(HubTicketOptions.SectionName).Get<HubTicketOptions>() ?? new();

        check(o.TicketSeconds is >= 5 and <= 120, area, "TicketSeconds",
            $"Hub tickets live {o.TicketSeconds}s.",
            "Hub:TicketSeconds must be between 5 and 120: long enough to open a connection, short enough that a logged URL is useless.");
        check(o.MaxTicketsPerUser >= 2, area, "MaxTicketsPerUser",
            "A user may hold enough tickets to connect.",
            "Hub:MaxTicketsPerUser must be at least 2: one connection needs a ticket for negotiate and one for the WebSocket.");
        check(o.MaxTickets >= o.MaxTicketsPerUser, area, "MaxTickets",
            "The ticket store holds at least one user's quota.", "Hub:MaxTickets must be at least Hub:MaxTicketsPerUser.");
        check(config.GetValue("RateLimit:TicketPerMinute", 30) >= 2, area, "TicketRateLimit",
            "RateLimit:TicketPerMinute allows a connection.", "RateLimit:TicketPerMinute must be at least 2 (a connection needs two tickets).");
    }

    private void ValidateChat(Verify check)
    {
        const string area = "Chat";
        var o = config.GetSection(ChatOptions.SectionName).Get<ChatOptions>() ?? new();

        check(o.MaxConcurrentJobs >= 1 && o.QueueCapacity >= 1, area, "Capacity",
            "Concurrent jobs and queue capacity are at least 1.", "Chat:MaxConcurrentJobs and Chat:QueueCapacity must be at least 1.");
        check(o.MaxJobsPerTenant >= 1 && o.FinalEventTimeoutSeconds >= 1, area, "TenantLimit",
            $"One tenant may have {o.MaxJobsPerTenant} requests in flight.",
            "Chat:MaxJobsPerTenant and Chat:FinalEventTimeoutSeconds must be at least 1.");
        check(o.MaxJobsPerTenant < o.MaxConcurrentJobs + o.QueueCapacity, area, "TenantLimitProtects",
            "The per-tenant limit is below total capacity, so one tenant cannot take all of it.",
            "Chat:MaxJobsPerTenant is not below Chat:MaxConcurrentJobs plus Chat:QueueCapacity, so it cannot stop one tenant taking every slot.",
            warnOnly: true);
        check(o.HeartbeatSeconds >= 1 && o.JobTimeoutSeconds >= 1 && o.DeltaFlushMilliseconds >= 0, area, "Timing",
            "Heartbeat, job timeout and flush interval are valid.",
            "Chat:HeartbeatSeconds and Chat:JobTimeoutSeconds must be at least 1, and Chat:DeltaFlushMilliseconds not negative.");
        check(o.KeepAliveSeconds >= 1 && o.ClientTimeoutSeconds >= 2 * o.KeepAliveSeconds, area, "SignalRTimeouts",
            "Client timeout is at least twice the keep-alive.",
            "Chat:ClientTimeoutSeconds must be at least twice Chat:KeepAliveSeconds (and keep-alive at least 1), or healthy connections will drop.");

        check(o.MaxReceiveMessageBytes >= ChatRequestValidator.MaxPartBytes, area, "ReceiveLimit",
            $"Hub message limit ({o.MaxReceiveMessageBytes} bytes) covers the largest single part of a request.",
            $"Chat:MaxReceiveMessageBytes must be at least {ChatRequestValidator.MaxPartBytes}; otherwise a valid long message makes the server close the connection.");

        var anthropic = config.GetSection(AnthropicOptions.SectionName).Get<AnthropicOptions>() ?? new();
        check(o.JobTimeoutSeconds >= anthropic.RateLimitMaxWaitSeconds, area, "JobTimeout",
            "Job timeout covers at least one maximum rate-limit wait.",
            "Chat:JobTimeoutSeconds is shorter than Anthropic:RateLimitMaxWaitSeconds, so jobs will time out while waiting out a rate limit.",
            warnOnly: true);
    }

    private void ValidateWeb(bool dev, Report pass, Report warn, Report fail, Verify check)
    {
        const string area = "Web";
        check(config.GetValue("RateLimit:ChatPerMinute", 30) >= 1, area, "RateLimit",
            "RateLimit:ChatPerMinute is at least 1.", "RateLimit:ChatPerMinute must be at least 1; every request would be rejected.");

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var bad = origins.Where(o => o == "*" || !Uri.TryCreate(o, UriKind.Absolute, out var u) || u.PathAndQuery != "/").ToList();
        if (bad.Count > 0)
        {
            fail(area, "Cors", $"Cors:AllowedOrigins has {bad.Count} invalid entr{(bad.Count == 1 ? "y" : "ies")}; use absolute origins such as https://app.example.com with no path or wildcard.");
        }
        else if (origins.Length == 0 && !dev)
        {
            warn(area, "Cors", "Cors:AllowedOrigins is empty; browsers on another origin will be blocked. Fine only if the UI is served from the same origin.");
        }
        else if (!dev && origins.Any(o => o.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            warn(area, "Cors", "Cors:AllowedOrigins contains http:// origins.");
        }
        else
        {
            pass(area, "Cors", $"{origins.Length} allowed origin(s).");
        }

        if (!dev && config["AllowedHosts"] is null or "*")
        {
            warn(area, "AllowedHosts", "AllowedHosts is '*'; restrict it to your host names.");
        }
        else
        {
            pass(area, "AllowedHosts", "AllowedHosts is restricted or the environment is Development.");
        }
    }

    private void ValidateHttp(bool dev, Report pass, Report warn, Report fail, Verify check)
    {
        const string area = "Http";
        HttpHardeningOptions o;
        try
        {
            o = config.GetSection(HttpHardeningOptions.SectionName).Get<HttpHardeningOptions>() ?? new();
        }
        catch (InvalidOperationException)
        {
            fail(area, "Binding", "The Http section has a value of the wrong type.");
            return;
        }

        var fwd = o.ForwardedHeaders;
        var badProxies = fwd.KnownProxies.Count(p => !System.Net.IPAddress.TryParse(p, out _));
        var badNetworks = fwd.KnownNetworks.Count(n => !System.Net.IPNetwork.TryParse(n, out _));
        if (badProxies + badNetworks > 0)
        {
            fail(area, "ForwardedHeaders",
                $"Http:ForwardedHeaders has {badProxies} invalid proxy address(es) and {badNetworks} invalid network(s); use IP addresses and CIDR ranges such as 10.0.0.0/16.");
        }
        else if (fwd.ForwardLimit < 1)
        {
            fail(area, "ForwardedHeaders", "Http:ForwardedHeaders:ForwardLimit must be at least 1.");
        }
        else if (fwd.KnownProxies.Length + fwd.KnownNetworks.Length == 0 && !dev)
        {
            warn(area, "ForwardedHeaders",
                "No trusted proxies are configured, so X-Forwarded-Proto from the gateway is ignored. Behind a TLS-terminating gateway every request looks like HTTP and is redirected; set Http:ForwardedHeaders:KnownNetworks.");
        }
        else
        {
            pass(area, "ForwardedHeaders",
                $"{fwd.KnownProxies.Length} trusted proxy address(es), {fwd.KnownNetworks.Length} trusted network(s).");
        }

        check(o.HttpsPort is >= 1 and <= 65535, area, "HttpsPort",
            "Http:HttpsPort is a valid port.", "Http:HttpsPort must be between 1 and 65535.");
        if (!dev && !o.HttpsRedirection)
        {
            warn(area, "HttpsRedirection", "Http:HttpsRedirection is off; make sure the gateway rejects or redirects plain HTTP.");
        }
        check(o.Hsts.MaxAgeDays >= 1, area, "Hsts",
            $"HSTS max-age is {o.Hsts.MaxAgeDays} day(s).", "Http:Hsts:MaxAgeDays must be at least 1.");

        var limit = o.MaxRequestBodyBytes;
        if (limit < 16 * 1024 || limit > HttpHardeningOptions.MaxAllowedRequestBodyBytes)
        {
            fail(area, "MaxRequestBodyBytes",
                $"Http:MaxRequestBodyBytes must be between 16384 and {HttpHardeningOptions.MaxAllowedRequestBodyBytes}.");
        }
        else if (limit < HttpHardeningOptions.MinRequestBodyBytes)
        {
            warn(area, "MaxRequestBodyBytes",
                $"Http:MaxRequestBodyBytes is below {HttpHardeningOptions.MinRequestBodyBytes}, so a full-length conversation sent over REST will be rejected with 413.");
        }
        else
        {
            pass(area, "MaxRequestBodyBytes", $"API request bodies are limited to {limit} bytes.");
        }

        var badOrigins = o.Csp.AuthorityOrigins.Count(origin => !HttpHardeningOptions.IsHttpsOrigin(origin));
        if (badOrigins > 0)
        {
            fail(area, "Csp", $"Http:Csp:AuthorityOrigins has {badOrigins} invalid entr{(badOrigins == 1 ? "y" : "ies")}; use https origins with no path, such as https://contoso.ciamlogin.com.");
        }
        else if (!string.IsNullOrWhiteSpace(o.Csp.Policy))
        {
            warn(area, "Csp", "Http:Csp:Policy replaces the generated Content-Security-Policy; review it with every frontend change.");
        }
        else
        {
            pass(area, "Csp", $"SPA policy allows {o.ResolveAuthorityOrigins(config).Count} sign-in origin(s).");
        }
    }
}
