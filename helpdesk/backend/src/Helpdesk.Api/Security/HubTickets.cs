using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Helpdesk.Api.Chat;
using Helpdesk.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Helpdesk.Api.Security;

/// <summary>
/// Browsers cannot set an Authorization header on a WebSocket, so the hub credential travels in the URL, where
/// proxies and load balancers log it. To keep the real access token out of the URL the browser exchanges it, over
/// ordinary HTTP with a header, for a ticket that is valid for seconds and for one use. Only the ticket is in the URL.
/// </summary>
public sealed class HubTicketOptions
{
    public const string SectionName = "Hub";

    /// <summary>How long a ticket can be used after it is issued. It is also capped by the access token's own expiry.</summary>
    public int TicketSeconds { get; set; } = 30;

    /// <summary>Tickets one user may hold unused at once. A connection needs two (negotiate and the WebSocket).</summary>
    public int MaxTicketsPerUser { get; set; } = 10;

    /// <summary>Unused tickets held in memory across all users.</summary>
    public int MaxTickets { get; set; } = 10_000;
}

/// <summary>What a ticket stands for: the caller, and when their access token stops being valid.</summary>
public sealed record HubTicketClaims(string TenantId, string UserId, DateTimeOffset TokenExpiresUtc);

public sealed record IssuedTicket(string Value, TimeSpan Lifetime);

/// <summary>
/// Issues and redeems hub tickets. The implementation here is in memory, so it only works on a single instance;
/// a deployment with several instances needs a shared implementation of this interface.
/// </summary>
public interface IHubTicketStore
{
    /// <summary>Returns null when the caller or the store is at its limit.</summary>
    IssuedTicket? Issue(HubTicketClaims claims);

    /// <summary>Returns the claims once. Unknown, expired and already-used tickets all return null.</summary>
    HubTicketClaims? Redeem(string ticket);
}

public sealed class InMemoryHubTicketStore(IOptions<HubTicketOptions> options, TimeProvider time) : IHubTicketStore
{
    private sealed record Entry(HubTicketClaims Claims, DateTimeOffset ExpiresUtc);

    private readonly HubTicketOptions _options = options.Value;
    // Keyed by a hash, so a memory dump or a bug that logs the store never reveals a usable ticket.
    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);
    private readonly Lock _issueLock = new();

    public IssuedTicket? Issue(HubTicketClaims claims)
    {
        var now = time.GetUtcNow();
        var lifetime = TimeSpan.FromSeconds(Math.Max(1, _options.TicketSeconds));
        var untilTokenExpires = claims.TokenExpiresUtc - now;
        if (untilTokenExpires < lifetime) lifetime = untilTokenExpires;
        if (lifetime <= TimeSpan.Zero) return null;

        var value = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        lock (_issueLock) // limits are checked and the ticket added as one step, so concurrent mints cannot overshoot
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
        return new IssuedTicket(value, lifetime);
    }

    public HubTicketClaims? Redeem(string ticket)
    {
        // TryRemove is atomic, so two simultaneous uses of one ticket cannot both succeed.
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(Hash(ticket), out var entry)) return null;
        return entry.ExpiresUtc > time.GetUtcNow() ? entry.Claims : null;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Authenticates requests to the hub with a ticket (from the <c>access_token</c> query parameter or a Bearer
/// header, whichever the SignalR client used). A raw JWT is not a ticket and is refused. The resulting
/// authentication carries the access token's expiry so the hub closes the connection when it passes.
/// </summary>
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
            new Claim(JwtTenantProvider.TenantClaim, claims.TenantId),
            new Claim(ClaimTypes.NameIdentifier, claims.UserId)
        ], SchemeName);
        var properties = new AuthenticationProperties { ExpiresUtc = claims.TokenExpiresUtc };
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), properties, SchemeName)));
    }
}
