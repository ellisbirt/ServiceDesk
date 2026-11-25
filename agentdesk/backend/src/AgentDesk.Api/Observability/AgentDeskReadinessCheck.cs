using AgentDesk.Core.Abstractions;
using AgentDesk.Infrastructure.Cosmos;
using AgentDesk.Infrastructure.Intake;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentDesk.Api.Observability;

public sealed class AgentDeskReadinessCheck(
    IConfiguration configuration,
    IServiceProvider services,
    IEnumerable<IHostedService> hostedServices,
    IBlobStore blobs) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.Equals(configuration["Storage:Provider"], "Cosmos", StringComparison.OrdinalIgnoreCase))
            {
                var containers = services.GetService<CosmosContainers>();
                if (containers is null)
                    return HealthCheckResult.Unhealthy("Cosmos storage is not registered.");
                await containers.Tickets.ReadContainerAsync(cancellationToken: cancellationToken);
                await containers.Kb.ReadContainerAsync(cancellationToken: cancellationToken);
                await containers.Attachments.ReadContainerAsync(cancellationToken: cancellationToken);
            }

            await blobs.CheckHealthAsync(cancellationToken);

            var intakeProvider = configuration["Intake:Provider"] ?? "None";
            if (string.Equals(intakeProvider, "StorageQueue", StringComparison.OrdinalIgnoreCase)
                && !hostedServices.OfType<StorageQueueIntakeConsumer>().Any(worker => worker.IsReady))
            {
                return HealthCheckResult.Unhealthy("The storage-queue intake worker is not running.");
            }
            if (string.Equals(intakeProvider, "ServiceBus", StringComparison.OrdinalIgnoreCase)
                && !hostedServices.OfType<ServiceBusIntakeConsumer>().Any(worker => worker.IsReady))
            {
                return HealthCheckResult.Unhealthy("The Service Bus intake worker is not running.");
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("A configured dependency is unavailable.", ex);
        }
    }
}
