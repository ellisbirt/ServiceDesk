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
        Assert.Equal(1, roundTrip.SchemaVersion);
    }

    [Fact]
    public void Event_DeserializesVersionlessPayloads_AndOmitsNullOptionalFields()
    {
        const string legacy = """
            {"eventId":"6f1c2b0e-0000-4000-8000-000000000001","tenantId":"t","userId":"u",
             "conversationId":"6f1c2b0e-0000-4000-8000-000000000002","correlationId":"c","sanitizedPayload":"p",
             "timestampUtc":"2026-01-01T00:00:00Z","futureField":true}
            """;
        var evt = JsonSerializer.Deserialize<ProcessPrivateCorpTicketEvent>(legacy)!;
        Assert.Equal(1, evt.SchemaVersion);
        Assert.Null(evt.Subject);
        Assert.Null(evt.LaneConfidence);

        var json = JsonSerializer.Serialize(Event());
        Assert.Contains("\"schemaVersion\":1", json);
        Assert.DoesNotContain("laneConfidence", json);
        Assert.DoesNotContain("subject", json);
    }

    [Theory]
    [InlineData(null, "(no subject)")]
    [InlineData("   ", "(no subject)")]
    [InlineData("\n\n  VPN   is down \nmore detail", "VPN is down")]
    public void TicketSubject_UsesFirstLine(string? input, string expected) =>
        Assert.Equal(expected, TicketSubject.FromText(input));

    [Fact]
    public void TicketSubject_TruncatesOnWordBoundary()
    {
        var subject = TicketSubject.FromText(string.Join(' ', Enumerable.Repeat("word", 60)));
        Assert.True(subject.Length <= TicketSubject.MaxLength);
        Assert.EndsWith("word\u2026", subject);
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
