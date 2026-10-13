using System.Text.Json;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Messaging;
using Helpdesk.Shared.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Helpdesk.Api.Tests;

public class QueuePublisherTests
{
    private static ProcessPrivateCorpTicketEvent Event() => new()
    {
        TenantId = "tenant-1", UserId = "u1", ConversationId = Guid.NewGuid(),
        CorrelationId = "chat-correlation-1", SanitizedPayload = "VPN down"
    };

    private sealed class RecordingSender : ServiceBusSender
    {
        public List<ServiceBusMessage> Sent { get; } = [];
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeQueueClient : QueueClient
    {
        public int CreateCalls { get; private set; }
        public List<string> Messages { get; } = [];

        public override Task<Response> CreateIfNotExistsAsync(
            IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult<Response>(null!);
        }

        public override Task<Response<SendReceipt>> SendMessageAsync(string messageText, CancellationToken cancellationToken = default)
        {
            Messages.Add(messageText);
            return Task.FromResult<Response<SendReceipt>>(null!);
        }
    }

    [Fact]
    public async Task ServiceBus_SetsTenantPartitionKey_AndProperties()
    {
        var sender = new RecordingSender();
        var evt = Event();
        await new ServiceBusTicketPublisher(sender).PublishAsync(evt);

        var msg = Assert.Single(sender.Sent);
        Assert.Equal("tenant-1", msg.PartitionKey);
        Assert.Equal("tenant-1", msg.ApplicationProperties["TenantId"]);
        Assert.Equal("PRIVATE_CORP", msg.ApplicationProperties["ActionLane"]);
        Assert.Equal("chat-correlation-1", msg.ApplicationProperties["CorrelationId"]);
        Assert.Equal(evt.EventId.ToString(), msg.MessageId);
    }

    [Fact]
    public async Task StorageQueue_SendsJsonBody_AndEnsuresQueueOnce()
    {
        var queue = new FakeQueueClient();
        var publisher = new StorageQueueTicketPublisher(queue);
        var evt = Event();

        await publisher.PublishAsync(evt);
        await publisher.PublishAsync(Event());

        Assert.Equal(1, queue.CreateCalls);
        Assert.Equal(2, queue.Messages.Count);
        var roundTrip = JsonSerializer.Deserialize<ProcessPrivateCorpTicketEvent>(queue.Messages[0])!;
        Assert.Equal(evt.EventId, roundTrip.EventId);
        Assert.Equal("tenant-1", roundTrip.TenantId);
        Assert.Equal("chat-correlation-1", roundTrip.CorrelationId);
    }

    public sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static ITicketQueuePublisher Resolve(string env, Dictionary<string, string?> settings)
    {
        settings.TryAdd("Anthropic:ApiKey", "k");
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHelpdeskInfrastructure(config, new Env(env));
        return services.BuildServiceProvider().GetRequiredService<ITicketQueuePublisher>();
    }

    [Fact]
    public void Development_DefaultsToStorageQueue()
    {
        var p = Resolve("Development", new() { ["StorageQueue:ConnectionString"] = "UseDevelopmentStorage=true" });
        Assert.IsType<StorageQueueTicketPublisher>(p);
    }

    [Fact]
    public void ServiceBus_CanBeSelectedExplicitly()
    {
        var p = Resolve("Development", new()
        {
            ["Queue:Provider"] = "ServiceBus",
            ["ServiceBus:ConnectionString"] =
                "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=Zm9v"
        });
        Assert.IsType<ServiceBusTicketPublisher>(p);
    }

    [Fact]
    public void NonDevelopment_RequiresExplicitProvider() =>
        Assert.Throws<InvalidOperationException>(() => Resolve("Production", []));

    [Fact]
    public void UnknownProvider_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Resolve("Development", new() { ["Queue:Provider"] = "Kafka" }));

    [Fact]
    public void StorageQueue_WithoutConnection_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Resolve("Development", []));
}
