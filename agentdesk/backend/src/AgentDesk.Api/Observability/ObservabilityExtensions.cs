using Helpdesk.Dlp;
using Helpdesk.Llm;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace AgentDesk.Api.Observability;

public static class ObservabilityExtensions
{
    public const string ServiceName = "AgentDesk.Api";

    /// <summary>Activity sources owned by AgentDesk (the intake consumer and the AI assist service).</summary>
    public static readonly string[] Sources = ["AgentDesk.Intake", "AgentDesk.Assist"];

    /// <summary>
    /// Serilog for logs (text in Development, compact JSON elsewhere) and OpenTelemetry for traces and metrics.
    /// Traces and metrics are exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    public static WebApplicationBuilder AddAgentDeskObservability(this WebApplicationBuilder builder)
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
        // Keeps the process-wide Log.Logger untouched so parallel test hosts do not share a logger.
        preserveStaticLogger: true);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                .AddSource(Sources)
                .AddSource(LlmTelemetry.Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => ctx.Request.Path != "/healthz")
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddMeter(LlmTelemetry.Name)
                .AddMeter(DlpTelemetry.Name)
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
