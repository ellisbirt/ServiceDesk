using System.Text.Json;
using Azure.Storage.Queues;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Events;

namespace Helpdesk.Infrastructure.Messaging;

public class StorageQueueOptions
{
    public const string SectionName = "StorageQueue";

    /// <summary>"UseDevelopmentStorage=true" targets the Azurite emulator.</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>e.g. https://account.queue.core.windows.net - used with managed identity when no connection string is set.</summary>
    public string QueueServiceUri { get; set; } = "";
    public string QueueName { get; set; } = "private-corp-tickets";
}

/// <summary>
/// Storage queues have no partition key or message properties, so the tenant travels in the JSON body
/// (<see cref="ProcessPrivateCorpTicketEvent.TenantId"/>); consumers must read it from there.
/// </summary>
public sealed class StorageQueueTicketPublisher(QueueClient queue) : ITicketQueuePublisher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _queueEnsured;

    public async Task PublishAsync(ProcessPrivateCorpTicketEvent ticketEvent, CancellationToken cancellationToken = default)
    {
        await EnsureQueueAsync(cancellationToken);
        await queue.SendMessageAsync(JsonSerializer.Serialize(ticketEvent), cancellationToken);
    }

    private async Task EnsureQueueAsync(CancellationToken ct)
    {
        if (_queueEnsured) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_queueEnsured) return;
            await queue.CreateIfNotExistsAsync(cancellationToken: ct);
            _queueEnsured = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
