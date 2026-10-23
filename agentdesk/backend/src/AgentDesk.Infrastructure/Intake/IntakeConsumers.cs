using System.Diagnostics;
using System.Text.Json;
using AgentDesk.Core.Services;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Helpdesk.Shared.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentDesk.Infrastructure.Intake;

public sealed class IntakeOptions
{
    public const string Section = "Intake";

    /// <summary>None (disabled), StorageQueue or ServiceBus. Must match the intake's <c>Queue:Provider</c>.</summary>
    public string Provider { get; set; } = "None";
    public string QueueName { get; set; } = "private-corp-tickets";
    /// <summary>Storage queue connection string (Azurite: <c>UseDevelopmentStorage=true</c>).</summary>
    public string? ConnectionString { get; set; }
    /// <summary>Queue service URI for managed identity (Storage Queue).</summary>
    public string? QueueServiceUri { get; set; }
    /// <summary>Service Bus fully qualified namespace for managed identity.</summary>
    public string? FullyQualifiedNamespace { get; set; }
    /// <summary>After this many failed deliveries a storage-queue message is moved to <c>{QueueName}-poison</c>.</summary>
    public int MaxDeliveries { get; set; } = 5;
    public int PollIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 16;
    public int VisibilityTimeoutSeconds { get; set; } = 60;
}

public enum IntakeOutcome { Created, Duplicate, Invalid }

/// <summary>Maps an intake queue event to a ticket. Shared by both queue transports so behaviour is identical.</summary>
public sealed class IntakeEventProcessor(TicketService tickets, ILogger<IntakeEventProcessor> logger)
{
    public static readonly ActivitySource ActivitySource = new("AgentDesk.Intake");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IntakeOutcome> ProcessAsync(string body, CancellationToken ct)
    {
        ProcessPrivateCorpTicketEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ProcessPrivateCorpTicketEvent>(body, Json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Intake message is not valid JSON ({Error}); it will be dead-lettered", ex.Message);
            return IntakeOutcome.Invalid;
        }

        var problem = Validate(evt);
        if (problem is not null || evt is null)
        {
            logger.LogWarning("Intake message rejected: {Problem}; it will be dead-lettered", problem);
            return IntakeOutcome.Invalid;
        }

        using var activity = ActivitySource.StartActivity("intake.import");
        activity?.SetTag("agentdesk.tenant", evt.TenantId);
        activity?.SetTag("agentdesk.event_id", evt.EventId);
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = evt.CorrelationId, ["TenantId"] = evt.TenantId, ["EventId"] = evt.EventId
        });

        // The summary and the conversation arrive as separate fields. Events from before they existed carry only
        // the plain-text payload, which then stays the description.
        var intake = new IntakeTicket(
            evt.TenantId,
            evt.EventId.ToString("D"),
            evt.UserId,
            string.IsNullOrWhiteSpace(evt.Subject) ? TicketSubject.FromText(evt.SanitizedPayload) : evt.Subject.Trim(),
            string.IsNullOrWhiteSpace(evt.Summary) ? evt.SanitizedPayload : evt.Summary,
            Severity: null,
            Category: null,
            evt.ConversationId.ToString("D"),
            evt.CorrelationId,
            evt.ConversationSummary);

        var (ticket, created) = await tickets.ImportFromIntakeAsync(intake, ct);
        logger.LogInformation(created ? "Created ticket {Number} from intake" : "Intake event already imported as {Number}", ticket.Number);
        return created ? IntakeOutcome.Created : IntakeOutcome.Duplicate;
    }

    internal static string? Validate(ProcessPrivateCorpTicketEvent? e)
    {
        if (e is null) return "empty body";
        if (e.SchemaVersion < 1 || e.SchemaVersion > ProcessPrivateCorpTicketEvent.CurrentSchemaVersion)
            return $"unsupported schemaVersion {e.SchemaVersion}";
        if (e.EventId == Guid.Empty) return "missing eventId";
        if (string.IsNullOrWhiteSpace(e.TenantId) || e.TenantId.Length > 128) return "missing or invalid tenantId";
        if (string.IsNullOrWhiteSpace(e.UserId) || e.UserId.Length > 256) return "missing or invalid userId";
        if (string.IsNullOrWhiteSpace(e.SanitizedPayload)) return "empty sanitizedPayload";
        return null;
    }
}

/// <summary>Polls an Azure Storage queue. Bodies are plain JSON (the intake does not Base64-encode them).</summary>
public sealed class StorageQueueIntakeConsumer(
    IServiceScopeFactory scopes,
    IOptions<IntakeOptions> options,
    ILogger<StorageQueueIntakeConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var (queue, poison) = CreateClients(o);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                await poison.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Intake queue {Queue} is unreachable; retrying in 15s", o.QueueName);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                QueueMessage[] messages = await queue.ReceiveMessagesAsync(Math.Clamp(o.BatchSize, 1, 32),
                    TimeSpan.FromSeconds(o.VisibilityTimeoutSeconds), stoppingToken);
                foreach (var message in messages)
                {
                    await HandleAsync(queue, poison, message, o, stoppingToken);
                    handled++;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Intake queue receive failed");
            }
            if (handled == 0) await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, o.PollIntervalSeconds)), stoppingToken);
        }
    }

    private async Task HandleAsync(QueueClient queue, QueueClient poison, QueueMessage message, IntakeOptions o, CancellationToken ct)
    {
        var body = message.Body.ToString();
        if (message.DequeueCount > o.MaxDeliveries)
        {
            logger.LogError("Intake message {MessageId} failed {Count} times; moving to {Poison}", message.MessageId, message.DequeueCount - 1, poison.Name);
            await poison.SendMessageAsync(body, ct);
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<IntakeEventProcessor>().ProcessAsync(body, ct);
            if (outcome == IntakeOutcome.Invalid) await poison.SendMessageAsync(body, ct);
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Leave the message; it becomes visible again after the visibility timeout and is retried.
            logger.LogWarning(ex, "Intake message {MessageId} failed (delivery {Count}); it will be retried", message.MessageId, message.DequeueCount);
        }
    }

    private static (QueueClient Queue, QueueClient Poison) CreateClients(IntakeOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
            return (new QueueClient(o.ConnectionString, o.QueueName), new QueueClient(o.ConnectionString, o.QueueName + "-poison"));
        if (string.IsNullOrWhiteSpace(o.QueueServiceUri))
            throw new InvalidOperationException("Intake:ConnectionString or Intake:QueueServiceUri must be set for the StorageQueue provider.");
        var service = new QueueServiceClient(new Uri(o.QueueServiceUri), new DefaultAzureCredential());
        return (service.GetQueueClient(o.QueueName), service.GetQueueClient(o.QueueName + "-poison"));
    }
}

/// <summary>Receives from a Service Bus queue. Invalid messages are dead-lettered; failures are abandoned and retried up to MaxDeliveryCount.</summary>
public sealed class ServiceBusIntakeConsumer(
    IServiceScopeFactory scopes,
    IOptions<IntakeOptions> options,
    ILogger<ServiceBusIntakeConsumer> logger) : BackgroundService
{
    private ServiceBusClient? client;
    private ServiceBusProcessor? processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        client = !string.IsNullOrWhiteSpace(o.ConnectionString)
            ? new ServiceBusClient(o.ConnectionString)
            : !string.IsNullOrWhiteSpace(o.FullyQualifiedNamespace)
                ? new ServiceBusClient(o.FullyQualifiedNamespace, new DefaultAzureCredential())
                : throw new InvalidOperationException("Intake:ConnectionString or Intake:FullyQualifiedNamespace must be set for the ServiceBus provider.");

        processor = client.CreateProcessor(o.QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4,
            PrefetchCount = 8
        });
        processor.ProcessMessageAsync += async args =>
        {
            using var scope = scopes.CreateScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<IntakeEventProcessor>()
                .ProcessAsync(args.Message.Body.ToString(), args.CancellationToken);
            if (outcome == IntakeOutcome.Invalid)
                await args.DeadLetterMessageAsync(args.Message, "InvalidEvent", "The intake event failed validation.", args.CancellationToken);
            else
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        };
        processor.ProcessErrorAsync += args =>
        {
            logger.LogWarning(args.Exception, "Service Bus intake error ({Source})", args.ErrorSource);
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (processor is not null) await processor.StopProcessingAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
        if (processor is not null) await processor.DisposeAsync();
        if (client is not null) await client.DisposeAsync();
    }
}
