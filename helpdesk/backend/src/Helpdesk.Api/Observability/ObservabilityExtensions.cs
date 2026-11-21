using Helpdesk.Core;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace Helpdesk.Api.Observability;

public static class ObservabilityExtensions
{
    public const string ServiceName = "Helpdesk.Api";

    /// <summary>
    /// Serilog for logs and OpenTelemetry for traces and metrics.
    /// Levels come from the <c>Serilog</c> config section. Logs go to the console: readable text in Development,
    /// compact JSON elsewhere (one event per line, for a collector or the platform's log pipeline).
    /// OTLP export is enabled per signal when its common or signal-specific endpoint is configured.
    /// </summary>
    public static WebApplicationBuilder AddHelpdeskObservability(this WebApplicationBuilder builder)
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
        // Each host keeps its own logger instead of replacing the process-wide Log.Logger. Nothing here uses the
        // static logger (request logging is given this host's logger in Program.cs), and it lets several hosts, such as
        // parallel test hosts, run in one process without their log output mixing.
        preserveStaticLogger: true);

        var exportTraces = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
            || !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"]);
        var exportMetrics = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
            || !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddSource(Telemetry.Name)
                .AddSource(LlmTelemetry.Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    ctx.Request.Path != "/healthz" && ctx.Request.Path != "/readyz")
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddMeter(Telemetry.Name)
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
