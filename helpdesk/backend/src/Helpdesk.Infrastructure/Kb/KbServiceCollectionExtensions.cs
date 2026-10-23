using Helpdesk.Core.Interfaces;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Kb;

public static class KbServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeBase(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<KbOptions>(config.GetSection(KbOptions.SectionName));
        var kb = config.GetSection(KbOptions.SectionName).Get<KbOptions>() ?? new();

        if (string.IsNullOrWhiteSpace(kb.Provider))
        {
            services.AddSingleton<IKnowledgeBaseSearch, DisabledKnowledgeBaseSearch>();
            return services;
        }
        if (!string.Equals(kb.Provider, KbProviders.Cosmos, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown Kb:Provider '{kb.Provider}'. Use '{KbProviders.Cosmos}' or leave it empty to disable.");
        }
        if (string.IsNullOrWhiteSpace(kb.Cosmos.ConnectionString) && string.IsNullOrWhiteSpace(kb.Cosmos.Endpoint))
        {
            throw new InvalidOperationException("Kb:Cosmos:ConnectionString or Kb:Cosmos:Endpoint is required when Kb:Provider is Cosmos.");
        }

        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => CosmosKbSource.CreateClient(kb.Cosmos));
        services.AddSingleton<IKbSource>(sp => new CosmosKbSource(
            sp.GetRequiredService<CosmosClient>().GetContainer(kb.Cosmos.Database, kb.Cosmos.Container),
            sp.GetRequiredService<ILogger<CosmosKbSource>>()));
        services.AddHostedService<CosmosKbWarmup>();
        services.AddSingleton<IKnowledgeBaseSearch, KnowledgeBaseSearch>();
        return services;
    }
}
