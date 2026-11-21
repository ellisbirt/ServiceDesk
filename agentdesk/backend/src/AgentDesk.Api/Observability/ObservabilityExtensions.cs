using Helpdesk.Dlp;
using Helpdesk.Llm;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace AgentDesk.Api.Observability;

public static class ObservabilityExtensions
{
    public const string ServiceName = "AgentDesk.Api";

    /// <summary>Activity sources owned by AgentDesk (the intake consumer and the AI assist service).</summary>
    public static readonly string[] Sources = ["AgentDesk.Intake", "AgentDesk.Assist"];

    /// <summary>
    /// Serilog for logs (text in Development, compact JSON elsewhere) and OpenTelemetry for traces and metrics.
    /// OTLP export is enabled per signal when its common or signal-specific endpoint is configured.
    /// </summary>
    public static WebApplicationBuilder AddAgentDeskObservability(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"]
            ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var protocolSetting = builder.Configuration["OTEL_EXPORTER_OTLP_LOGS_PROTOCOL"]
            ?? builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
        var otlpProtocol = protocolSetting switch
        {
            "http/protobuf" => OtlpProtocol.HttpProtobuf,
            null or "grpc" => OtlpProtocol.Grpc,
            _ => throw new InvalidOperationException($"Unsupported OTLP protocol '{protocolSetting}'.")
        };
        if (!string.IsNullOrWhiteSpace(otlpEndpoint)
            && (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var parsedEndpoint)
                || parsedEndpoint.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("The OTLP logs endpoint must be an absolute HTTP or HTTPS URI.");

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

            if (Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpoint))
            {
                logger.WriteTo.OpenTelemetry(options =>
                {
                    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"]))
                    {
                        options.Endpoint = null;
                        options.LogsEndpoint = endpoint.ToString();
                    }
                    else
                    {
                        options.Endpoint = endpoint.ToString();
                    }
                    options.Protocol = otlpProtocol;
                    options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
                    options.ResourceAttributes = CreateLogResourceAttributes(
                        builder.Configuration, context.HostingEnvironment.EnvironmentName);
                });
            }
        },
        // Keeps the process-wide Log.Logger untouched so parallel test hosts do not share a logger.
        preserveStaticLogger: true);

        var exportTraces = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
            || !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"]);
        var exportMetrics = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
            || !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddSource(Sources)
                .AddSource(LlmTelemetry.Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    ctx.Request.Path != "/healthz" && ctx.Request.Path != "/readyz")
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddMeter(LlmTelemetry.Name)
                .AddMeter(DlpTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (exportTraces) builder.Services.AddOpenTelemetry().WithTracing(t => t.AddOtlpExporter());
        if (exportMetrics) builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddOtlpExporter());
        return builder;
    }

    private static Dictionary<string, object> CreateLogResourceAttributes(IConfiguration configuration, string environment)
    {
        var attributes = new Dictionary<string, object>
        {
            ["service.name"] = ServiceName,
            ["service.version"] = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["deployment.environment.name"] = environment
        };
        var configured = configuration["OTEL_RESOURCE_ATTRIBUTES"];
        if (string.IsNullOrWhiteSpace(configured)) return attributes;

        foreach (var attribute in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = attribute.IndexOf('=');
            if (separator <= 0 || separator == attribute.Length - 1)
                throw new InvalidOperationException("OTEL_RESOURCE_ATTRIBUTES must contain comma-separated key=value pairs.");
            var key = attribute[..separator].Trim();
            if (key is not ("service.name" or "service.version" or "deployment.environment.name"))
                attributes[key] = attribute[(separator + 1)..].Trim();
        }
        return attributes;
    }
}
