using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AgentDesk.Api.Security;

/// <summary>
/// Browsers cannot put an Authorization header on a WebSocket, and the hub URL ends up in proxy logs, so when JWT auth
/// is enforced the console exchanges its access token (over normal HTTP) for a single-use ticket valid for seconds.
/// Only the ticket travels in the hub URL.
/// </summary>
public sealed class HubTicketOptions
{
    public const string SectionName = "Hub";
    public int TicketSeconds { get; set; } = 30;
    public int MaxTicketsPerUser { get; set; } = 10;
    public int MaxTickets { get; set; } = 10_000;
}

public sealed record HubTicketClaims(string TenantId, string UserId, string DisplayName, DateTimeOffset TokenExpiresUtc);

public sealed record IssuedHubTicket(string Value, TimeSpan Lifetime);

/// <summary>In-memory, so single-instance only. Several instances need a shared store behind this interface.</summary>
public interface IHubTicketStore
{
    IssuedHubTicket? Issue(HubTicketClaims claims);
    HubTicketClaims? Redeem(string ticket);
}

public sealed class InMemoryHubTicketStore(IOptions<HubTicketOptions> options, TimeProvider time) : IHubTicketStore
{
    private sealed record Entry(HubTicketClaims Claims, DateTimeOffset ExpiresUtc);

    private readonly HubTicketOptions _options = options.Value;
    // Keyed by hash so a dump of the store never reveals a usable ticket.
    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);
    private readonly Lock _issueLock = new();

    public IssuedHubTicket? Issue(HubTicketClaims claims)
    {
        var now = time.GetUtcNow();
        var lifetime = TimeSpan.FromSeconds(Math.Max(1, _options.TicketSeconds));
        var untilTokenExpires = claims.TokenExpiresUtc - now;
        if (untilTokenExpires < lifetime) lifetime = untilTokenExpires;
        if (lifetime <= TimeSpan.Zero) return null;

        var value = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        lock (_issueLock)
        {
            foreach (var (key, entry) in _tickets)
            {
                if (entry.ExpiresUtc <= now) _tickets.TryRemove(key, out _);
            }
            if (_tickets.Count >= _options.MaxTickets) return null;
            if (_tickets.Values.Count(e => e.Claims.TenantId == claims.TenantId && e.Claims.UserId == claims.UserId)
                >= _options.MaxTicketsPerUser)
            {
                return null;
            }
            _tickets[Hash(value)] = new Entry(claims, now + lifetime);
        }
        return new IssuedHubTicket(value, lifetime);
    }

    public HubTicketClaims? Redeem(string ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(Hash(ticket), out var entry)) return null;
        return entry.ExpiresUtc > time.GetUtcNow() ? entry.Claims : null;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Authenticates hub requests with a ticket from <c>access_token</c> (or a Bearer header). Raw JWTs are refused.</summary>
public sealed class HubTicketAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IHubTicketStore tickets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "HubTicket";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Query["access_token"].FirstOrDefault();
        if (string.IsNullOrEmpty(presented))
        {
            var header = Request.Headers.Authorization.FirstOrDefault();
            if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                presented = header["Bearer ".Length..].Trim();
            }
        }
        if (string.IsNullOrEmpty(presented)) return Task.FromResult(AuthenticateResult.NoResult());

        if (tickets.Redeem(presented) is not { } claims)
        {
            return Task.FromResult(AuthenticateResult.Fail("The hub ticket is invalid, expired or already used."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("tenant_id", claims.TenantId),
            new Claim(ClaimTypes.NameIdentifier, claims.UserId),
            new Claim("name", claims.DisplayName)
        ], SchemeName, "name", AgentDeskClaims.RoleClaim);
        var properties = new AuthenticationProperties { ExpiresUtc = claims.TokenExpiresUtc };
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), properties, SchemeName)));
    }
}
