using Helpdesk.Api.Chat;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Helpdesk.Api.Observability;

public sealed class HelpdeskReadinessCheck(
    ChatJobProcessor processor,
    ITicketQueuePublisher queuePublisher) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!processor.IsRunning)
            return HealthCheckResult.Unhealthy("The chat job processor is not running.");

        try
        {
            await queuePublisher.CheckHealthAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("The ticket queue is unavailable.", ex);
        }
    }
}
