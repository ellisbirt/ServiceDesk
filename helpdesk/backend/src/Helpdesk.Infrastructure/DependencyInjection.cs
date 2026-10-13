using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Queues;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Messaging;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddHelpdeskInfrastructure(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.Configure<AnthropicOptions>(config.GetSection(AnthropicOptions.SectionName));
        // Share the standard ANTHROPIC_API_KEY variable (also used by the evals) when no explicit key is configured.
        services.PostConfigure<AnthropicOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = config["ANTHROPIC_API_KEY"] ?? "";
        });

        var anthropic = config.GetSection(AnthropicOptions.SectionName).Get<AnthropicOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(anthropic.ApiKey)) anthropic.ApiKey = config["ANTHROPIC_API_KEY"] ?? "";
        var queueProvider = config.GetSection(QueueOptions.SectionName).Get<QueueOptions>()?.Provider;
        if (string.IsNullOrWhiteSpace(queueProvider))
        {
            queueProvider = env.IsDevelopment() ? QueueProviders.StorageQueue : null;
        }

        // Offline fallbacks must never silently run outside development.
        if (!env.IsDevelopment() && !anthropic.IsConfigured)
        {
            throw new InvalidOperationException("Anthropic:ApiKey is required outside Development.");
        }
        if (queueProvider is null)
        {
            throw new InvalidOperationException(
                $"Queue:Provider must be set to '{QueueProviders.StorageQueue}' or '{QueueProviders.ServiceBus}' outside Development.");
        }

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantProvider, JwtTenantProvider>();
        services.Configure<DlpOptions>(config.GetSection(DlpOptions.SectionName));
        services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();

        if (anthropic.IsConfigured)
        {
            services.AddHttpClient<IAnthropicClient, AnthropicClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
                http.BaseAddress = new Uri(o.BaseUrl);
                http.DefaultRequestHeaders.Add("x-api-key", o.ApiKey);
                http.DefaultRequestHeaders.Add("anthropic-version", o.AnthropicVersion);
                http.Timeout = Timeout.InfiniteTimeSpan; // owned by the resilience handler
            }).AddHelpdeskResilience(
                attemptTimeout: TimeSpan.FromSeconds(60), totalTimeout: TimeSpan.FromSeconds(150), callerHandlesRateLimits: true);
            services.AddScoped<IGuardrailRouterService, AnthropicGuardrailRouterService>();
            services.AddScoped<IPublicInferenceService, AnthropicPublicInferenceService>();
            services.AddScoped<IIntakeFormGenerator, AnthropicIntakeFormGenerator>();
        }
        else
        {
            services.AddScoped<IGuardrailRouterService, OfflineGuardrailRouterService>();
            services.AddScoped<IPublicInferenceService, OfflinePublicInferenceService>();
            services.AddScoped<IIntakeFormGenerator, OfflineIntakeFormGenerator>();
        }

        services.AddLiveSearch(config);

        switch (queueProvider)
        {
            case QueueProviders.StorageQueue:
                AddStorageQueue(services, config, env);
                break;
            case QueueProviders.ServiceBus:
                AddServiceBus(services, config);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Queue:Provider '{queueProvider}'. Use '{QueueProviders.StorageQueue}' or '{QueueProviders.ServiceBus}'.");
        }

        return services;
    }

    private static void AddStorageQueue(IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var o = config.GetSection(StorageQueueOptions.SectionName).Get<StorageQueueOptions>() ?? new();

        // Fail fast in development so a missing Azurite surfaces immediately instead of after long retries.
        var clientOptions = new QueueClientOptions();
        if (env.IsDevelopment()) clientOptions.Retry.MaxRetries = 0;

        QueueClient client;
        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
        {
            client = new QueueClient(o.ConnectionString, o.QueueName, clientOptions);
        }
        else if (!string.IsNullOrWhiteSpace(o.QueueServiceUri))
        {
            client = new QueueClient(
                new Uri($"{o.QueueServiceUri.TrimEnd('/')}/{o.QueueName}"), new DefaultAzureCredential(), clientOptions);
        }
        else
        {
            throw new InvalidOperationException(
                "StorageQueue:ConnectionString or StorageQueue:QueueServiceUri is required when Queue:Provider is StorageQueue.");
        }

        services.AddSingleton(client);
        services.AddSingleton<ITicketQueuePublisher, StorageQueueTicketPublisher>();
    }

    private static void AddServiceBus(IServiceCollection services, IConfiguration config)
    {
        var o = config.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new();
        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
        {
            services.AddSingleton(_ => new ServiceBusClient(o.ConnectionString));
        }
        else if (!string.IsNullOrWhiteSpace(o.FullyQualifiedNamespace))
        {
            services.AddSingleton(_ => new ServiceBusClient(o.FullyQualifiedNamespace, new DefaultAzureCredential()));
        }
        else
        {
            throw new InvalidOperationException(
                "ServiceBus:ConnectionString or ServiceBus:FullyQualifiedNamespace is required when Queue:Provider is ServiceBus.");
        }

        services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(o.QueueName));
        services.AddSingleton<ITicketQueuePublisher, ServiceBusTicketPublisher>();
    }
}
