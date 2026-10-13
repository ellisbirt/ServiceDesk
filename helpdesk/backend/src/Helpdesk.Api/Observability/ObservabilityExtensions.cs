using Helpdesk.Core;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace Helpdesk.Api.Observability;

public static class ObservabilityExtensions
{
    public const string ServiceName = "Helpdesk.Api";

    /// <summary>
    /// Serilog for logs and OpenTelemetry for traces and metrics.
    /// Levels come from the <c>Serilog</c> config section. Logs go to the console: readable text in Development,
    /// compact JSON elsewhere (one event per line, for a collector or the platform's log pipeline).
    /// Traces and metrics are exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; the other
    /// standard <c>OTEL_*</c> variables (headers, protocol, resource attributes, sampler) work as usual.
    /// </summary>
    public static WebApplicationBuilder AddHelpdeskObservability(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, logger) =>
        {
            logger
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", ServiceName)
                .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);

            if (context.HostingEnvironment.IsDevelopment())
            {
                logger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
            }
            else
            {
                logger.WriteTo.Console(new RenderedCompactJsonFormatter());
            }
        },
        // Each host keeps its own logger instead of replacing the process-wide Log.Logger. Nothing here uses the
        // static logger (request logging is given this host's logger in Program.cs), and it lets several hosts, such as
        // parallel test hosts, run in one process without their log output mixing.
        preserveStaticLogger: true);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                .AddSource(Telemetry.Name)
                .AddSource(LlmTelemetry.Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => ctx.Request.Path != "/healthz")
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddMeter(Telemetry.Name)
                .AddMeter(LlmTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }
        return builder;
    }
}
