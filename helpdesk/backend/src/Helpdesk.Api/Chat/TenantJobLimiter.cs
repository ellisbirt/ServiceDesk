using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Caps how many requests one tenant has in flight (<see cref="ChatOptions.MaxJobsPerTenant"/>). The SignalR queue and
/// the REST fallback both take a lease for the life of a request, so neither can be used to get around the cap.
/// In memory, so it counts per instance, like the queue it protects.
/// </summary>
public sealed class TenantJobLimiter(IOptions<ChatOptions> options)
{
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly int _limit = Math.Max(1, options.Value.MaxJobsPerTenant);

    /// <summary>Returns a lease to dispose when the request ends, or null when the tenant is at its cap.</summary>
    public IDisposable? TryAcquire(string tenantId)
    {
        lock (_gate)
        {
            var current = _inFlight.GetValueOrDefault(tenantId);
            if (current >= _limit) return null;
            _inFlight[tenantId] = current + 1;
        }
        return new Lease(this, tenantId);
    }

    public int InFlight(string tenantId)
    {
        lock (_gate) return _inFlight.GetValueOrDefault(tenantId);
    }

    private void Release(string tenantId)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(tenantId, out var current)) return;
            if (current <= 1) _inFlight.Remove(tenantId); // do not keep an entry for every tenant that ever called
            else _inFlight[tenantId] = current - 1;
        }
    }

    private sealed class Lease(TenantJobLimiter owner, string tenantId) : IDisposable
    {
        private int _released;

        // Releasing twice must not give back a place that belongs to another request.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(tenantId);
        }
    }
}
