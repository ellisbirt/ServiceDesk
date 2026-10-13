using Helpdesk.Api.Chat;

namespace Helpdesk.Api.Hosting;

/// <summary>Transport hardening settings (the <c>Http</c> section).</summary>
public sealed class HttpHardeningOptions
{
    public const string SectionName = "Http";
    public const long DefaultMaxRequestBodyBytes = 128 * 1024;
    public const long MaxAllowedRequestBodyBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Smallest body limit that still accepts a full-length conversation of plain ASCII text sent over REST: the
    /// message plus a full history, each with a signature and some JSON envelope. Longer non-ASCII conversations
    /// are split into parts over the hub, which has its own limit (<c>Chat:MaxReceiveMessageBytes</c>).
    /// </summary>
    public const long MinRequestBodyBytes =
        (ChatRequestValidator.MaxHistoryMessages + 1)
        * (ChatRequestValidator.MaxMessageLength + ChatRequestValidator.MaxSignatureLength + 128) + 1024;

    public ForwardedHeadersSettings ForwardedHeaders { get; set; } = new();

    /// <summary>Redirect plain HTTP to HTTPS outside Development. Health probes are never redirected.</summary>
    public bool HttpsRedirection { get; set; } = true;

    /// <summary>Port used in the HTTPS redirect. 443 suits a TLS-terminating gateway in front of the app.</summary>
    public int HttpsPort { get; set; } = 443;

    public HstsSettings Hsts { get; set; } = new();

    /// <summary>Largest request body accepted on <c>/api</c> paths; larger requests get 413.</summary>
    public long MaxRequestBodyBytes { get; set; } = DefaultMaxRequestBodyBytes;

    public CspSettings Csp { get; set; } = new();

    public sealed class ForwardedHeadersSettings
    {
        /// <summary>IP addresses of proxies allowed to set X-Forwarded-For and X-Forwarded-Proto.</summary>
        public string[] KnownProxies { get; set; } = [];

        /// <summary>CIDR ranges of proxies allowed to set the forwarded headers, e.g. <c>10.0.0.0/16</c>.</summary>
        public string[] KnownNetworks { get; set; } = [];

        /// <summary>How many proxy hops to unwind. Keep at 1 unless there is a chain of trusted proxies.</summary>
        public int ForwardLimit { get; set; } = 1;
    }

    public sealed class HstsSettings
    {
        public int MaxAgeDays { get; set; } = 365;
        public bool IncludeSubDomains { get; set; } = true;
        public bool Preload { get; set; }
    }

    public sealed class CspSettings
    {
        /// <summary>
        /// Origins of the sign-in authority the SPA talks to and frames for silent token renewal, e.g.
        /// <c>https://contoso.ciamlogin.com</c>. Defaults to the origin of <c>Auth:Authority</c>.
        /// </summary>
        public string[] AuthorityOrigins { get; set; } = [];

        /// <summary>Replaces the generated SPA policy entirely when set.</summary>
        public string? Policy { get; set; }
    }

    /// <summary>The configured authority origins, or the origin of <c>Auth:Authority</c> when none are configured.</summary>
    public IReadOnlyList<string> ResolveAuthorityOrigins(IConfiguration config)
    {
        if (Csp.AuthorityOrigins.Length > 0) return Csp.AuthorityOrigins;
        return Uri.TryCreate(config["Auth:Authority"], UriKind.Absolute, out var authority) && authority.Scheme == Uri.UriSchemeHttps
            ? [authority.GetLeftPart(UriPartial.Authority)]
            : [];
    }

    /// <summary>The Content-Security-Policy for the SPA document and its static files.</summary>
    public string SpaContentSecurityPolicy(IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(Csp.Policy)) return Csp.Policy;
        var authority = string.Join(' ', ResolveAuthorityOrigins(config));
        var connect = authority.Length > 0 ? $"'self' {authority}" : "'self'";
        var frame = authority.Length > 0 ? authority : "'none'";
        return "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
            + $"connect-src {connect}; frame-src {frame}; "
            + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    }

    /// <summary>True when the value is an absolute https origin with no path, query or fragment.</summary>
    public static bool IsHttpsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.PathAndQuery == "/"
        && uri.Fragment.Length == 0
        && !value.EndsWith('/');
}
