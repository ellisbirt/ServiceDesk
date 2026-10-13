using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Helpdesk.Shared.Events;

namespace Helpdesk.Infrastructure.Messaging;

public static class QueueProviders
{
    public const string ServiceBus = "ServiceBus";
    public const string StorageQueue = "StorageQueue";
}

public class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>"StorageQueue" or "ServiceBus". Required outside Development; defaults to StorageQueue (Azurite) in Development.</summary>
    public string Provider { get; set; } = "";
}

public class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public string ConnectionString { get; set; } = "";
    /// <summary>Used with managed identity (DefaultAzureCredential) when no connection string is set.</summary>
    public string FullyQualifiedNamespace { get; set; } = "";
    public string QueueName { get; set; } = "private-corp-tickets";
}

public sealed class ServiceBusTicketPublisher(ServiceBusSender sender) : ITicketQueuePublisher
{
    public Task PublishAsync(ProcessPrivateCorpTicketEvent ticketEvent, CancellationToken cancellationToken = default)
    {
        var message = new ServiceBusMessage(JsonSerializer.Serialize(ticketEvent))
        {
            MessageId = ticketEvent.EventId.ToString(),
            PartitionKey = ticketEvent.TenantId, // strict physical grouping per tenant
            ContentType = "application/json"
        };
        message.ApplicationProperties.Add("TenantId", ticketEvent.TenantId);
        message.ApplicationProperties.Add("ActionLane", ActionLanes.PrivateCorp);
        message.ApplicationProperties.Add("CorrelationId", ticketEvent.CorrelationId);

        return sender.SendMessageAsync(message, cancellationToken);
    }
}
