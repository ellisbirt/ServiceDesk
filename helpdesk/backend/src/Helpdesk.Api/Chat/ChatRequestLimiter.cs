using System.Threading.RateLimiting;
using Helpdesk.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Per-user request limit for hub invocations, which the HTTP rate limiter middleware does not see.
/// Uses the same <c>RateLimit:ChatPerMinute</c> setting as the REST endpoint but keeps its own counters.
/// </summary>
public sealed class ChatRequestLimiter(IConfiguration config) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimit:ChatPerMinute", 30),
            Window = TimeSpan.FromMinutes(1)
        }));

    public bool TryAcquire(string tenantId, string userId)
    {
        using var lease = _limiter.AttemptAcquire($"{tenantId}{JwtTenantProvider.KeySeparator}{userId}");
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
