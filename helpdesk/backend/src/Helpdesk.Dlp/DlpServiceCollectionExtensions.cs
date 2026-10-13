using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Dlp;

public static class DlpTelemetry
{
    public const string Name = "Helpdesk.Dlp";

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Redactions =
        Meter.CreateCounter<long>("helpdesk.redactions", description: "Sensitive values removed before leaving the service.");
}

public static class DlpServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ISensitiveDataRedactor"/> with policy overrides from the <c>Dlp</c> section.</summary>
    public static IServiceCollection AddHelpdeskDlp(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DlpOptions>(config.GetSection(DlpOptions.SectionName));
        services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();
        return services;
    }
}
