using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Services;
using AgentDesk.Infrastructure.Ai;
using AgentDesk.Infrastructure.Blobs;
using AgentDesk.Infrastructure.Cosmos;
using AgentDesk.Infrastructure.InMemory;
using AgentDesk.Infrastructure.Intake;
using Helpdesk.Dlp;
using Helpdesk.Llm;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentDesk.Infrastructure;

public static class StorageProviders
{
    public const string InMemory = "InMemory";
    public const string Cosmos = "Cosmos";
    public const string Azure = "Azure";
}

public static class DependencyInjection
{
    /// <summary>
    /// Registers storage (<c>Storage:Provider</c> = InMemory | Cosmos), blobs (<c>Blobs:Provider</c> = InMemory | Azure),
    /// the LLM providers, DLP, KB embeddings and the intake consumer (<c>Intake:Provider</c>).
    /// </summary>
    public static IServiceCollection AddAgentDeskInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ITicketNotifier, NullTicketNotifier>();
        services.TryAddSingleton<IAttachmentScanner, AcceptAllAttachmentScanner>();

        services.Configure<TicketOptions>(config.GetSection(TicketOptions.Section));
        services.Configure<AttachmentOptions>(config.GetSection(AttachmentOptions.Section));
        services.Configure<KbEmbeddingOptions>(config.GetSection(KbEmbeddingOptions.Section));
        services.Configure<BlobStorageOptions>(config.GetSection(BlobStorageOptions.Section));
        services.Configure<IntakeOptions>(config.GetSection(IntakeOptions.Section));

        services.AddScoped<TicketService>();
        services.AddScoped<KbService>();
        services.AddScoped<AttachmentService>();

        AddStorage(services, config);
        AddBlobs(services, config);

        services.AddLlmProviders(config.GetSection("Llm"));
        services.AddHelpdeskDlp(config);
        services.AddScoped<IKbEmbeddingService, KbEmbeddingService>();
        services.AddSingleton<KbEmbeddingSweep>();
        services.AddHostedService(sp => sp.GetRequiredService<KbEmbeddingSweep>());
        services.Configure<AssistOptions>(config.GetSection(AssistOptions.Section));
        services.AddScoped<TicketAssistService>();

        AddIntake(services, config);
        return services;
    }

    private static void AddStorage(IServiceCollection services, IConfiguration config)
    {
        var provider = config["Storage:Provider"] ?? StorageProviders.InMemory;
        switch (provider)
        {
            case StorageProviders.InMemory:
                services.AddSingleton<InMemoryDocumentStore>();
                services.AddSingleton<ITicketRepository, InMemoryTicketRepository>();
                services.AddSingleton<IKbRepository, InMemoryKbRepository>();
                services.AddSingleton<IAttachmentRepository, InMemoryAttachmentRepository>();
                break;
            case StorageProviders.Cosmos:
                services.Configure<CosmosOptions>(config.GetSection(CosmosOptions.Section));
                services.AddSingleton(sp => CosmosContainers.CreateClient(sp.GetRequiredService<IOptions<CosmosOptions>>().Value));
                services.AddSingleton(sp =>
                {
                    var o = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
                    var client = sp.GetRequiredService<CosmosClient>();
                    return new CosmosContainers(
                        client.GetContainer(o.Database, o.TicketsContainer),
                        client.GetContainer(o.Database, o.KbContainer),
                        client.GetContainer(o.Database, o.AttachmentsContainer));
                });
                // Registered before other hosted services so containers exist before consumers start.
                services.AddHostedService<CosmosInitializer>();
                services.AddSingleton<ITicketRepository, CosmosTicketRepository>();
                services.AddSingleton<IKbRepository, CosmosKbRepository>();
                services.AddSingleton<IAttachmentRepository, CosmosAttachmentRepository>();
                break;
            default:
                throw new InvalidOperationException($"Unknown Storage:Provider '{provider}'. Use '{StorageProviders.InMemory}' or '{StorageProviders.Cosmos}'.");
        }
    }

    private static void AddBlobs(IServiceCollection services, IConfiguration config)
    {
        var provider = config["Blobs:Provider"] ?? StorageProviders.InMemory;
        switch (provider)
        {
            case StorageProviders.InMemory:
                services.AddSingleton<InMemoryBlobStore>();
                services.AddSingleton<IBlobStore>(sp => sp.GetRequiredService<InMemoryBlobStore>());
                break;
            case StorageProviders.Azure:
                services.AddSingleton<IBlobStore, AzureBlobStore>();
                break;
            default:
                throw new InvalidOperationException($"Unknown Blobs:Provider '{provider}'. Use '{StorageProviders.InMemory}' or '{StorageProviders.Azure}'.");
        }
    }

    private static void AddIntake(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<IntakeEventProcessor>();
        var provider = config["Intake:Provider"] ?? "None";
        switch (provider)
        {
            case "None":
                break;
            case "StorageQueue":
                services.AddHostedService<StorageQueueIntakeConsumer>();
                break;
            case "ServiceBus":
                services.AddHostedService<ServiceBusIntakeConsumer>();
                break;
            default:
                throw new InvalidOperationException($"Unknown Intake:Provider '{provider}'. Use None, StorageQueue or ServiceBus.");
        }
    }
}
