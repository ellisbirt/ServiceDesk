using System.Diagnostics;
using System.Threading.RateLimiting;
using Helpdesk.Api;
using Helpdesk.Api.Observability;
using Serilog;
using Helpdesk.Api.Chat;
using Helpdesk.Api.Endpoints;
using Helpdesk.Api.Hosting;
using Helpdesk.Api.Security;
using Helpdesk.Api.Status;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddHelpdeskObservability();

var config = builder.Configuration;
var useDevAuth = builder.Environment.IsDevelopment() && config.GetValue<bool>("Auth:UseDevAuth");

if (useDevAuth)
{
    builder.Services
        .AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
}
else
{
    // The API takes the JWT in the Authorization header. The hub cannot (browsers cannot set headers on a
    // WebSocket) and its URL ends up in proxy logs, so it takes a short-lived single-use ticket instead, which the
    // client buys with its JWT from POST /api/v1/chat/ticket. A JWT presented to the hub is refused.
    builder.Services
        .AddAuthentication(o =>
        {
            o.DefaultScheme = RoutingScheme;
            o.DefaultChallengeScheme = RoutingScheme;
        })
        .AddPolicyScheme(RoutingScheme, displayName: null, o =>
            o.ForwardDefaultSelector = ctx => ctx.Request.Path.StartsWithSegments(ChatHub.Path)
                ? HubTicketAuthHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme)
        .AddScheme<AuthenticationSchemeOptions, HubTicketAuthHandler>(HubTicketAuthHandler.SchemeName, null)
        .AddJwtBearer(o =>
        {
            // Entra External ID authority, e.g. https://<tenant>.ciamlogin.com/<tenant-id>/v2.0
            o.Authority = config["Auth:Authority"];
            o.Audience = config["Auth:Audience"];
            // Entra puts app roles in the "roles" claim; inbound claim mapping is off, so name it explicitly.
            o.TokenValidationParameters.RoleClaimType = "roles";
        });
}

builder.Services.Configure<HubTicketOptions>(config.GetSection(HubTicketOptions.SectionName));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IHubTicketStore, InMemoryHubTicketStore>();

builder.Services.AddAuthorization(o =>
{
    // Every authenticated caller must carry the tenant claim; there is no tenant-less access. The tenant and user
    // ids must also be usable in composite keys (no ':'), so a caller with such an id is refused with 403 up
    // front, on the hub's negotiate request as well as on REST calls.
    o.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(JwtTenantProvider.TenantClaim)
        .RequireAssertion(ctx => JwtTenantProvider.HasValidIdentity(ctx.User))
        .Build();

    // The status report describes the whole deployment, so it is for platform operators, not tenant users.
    o.AddPolicy(StatusPolicy, p => p
        .RequireAuthenticatedUser()
        .RequireClaim(JwtTenantProvider.TenantClaim)
        .RequireAssertion(ctx => JwtTenantProvider.HasValidIdentity(ctx.User))
        .RequireRole(config["Status:RequiredRole"] is { Length: > 0 } role ? role : DefaultStatusRole));
});
builder.Services.AddSingleton<ConfigurationValidator>();

builder.Services.AddHelpdeskInfrastructure(config, builder.Environment);
builder.Services.Configure<ChatOptions>(config.GetSection(ChatOptions.SectionName));
var chat = config.GetSection(ChatOptions.SectionName).Get<ChatOptions>() ?? new();
builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(chat.KeepAliveSeconds);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(chat.ClientTimeoutSeconds);
    o.MaximumReceiveMessageSize = chat.MaxReceiveMessageBytes;
})
.AddJsonProtocol(o => o.PayloadSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);
builder.Services.Configure<ConversationSigningOptions>(config.GetSection(ConversationSigningOptions.SectionName));
builder.Services.AddSingleton<IConversationSigner, ConversationSigner>();
builder.Services.AddScoped<ChatPipeline>();
builder.Services.AddSingleton<TenantJobLimiter>();
builder.Services.AddSingleton<ChatJobQueue>();
builder.Services.AddSingleton<ChatRequestLimiter>();
builder.Services.AddSingleton<ChatJobProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatJobProcessor>());
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string UserKey(HttpContext ctx) =>
        $"{ctx.User.FindFirst(JwtTenantProvider.TenantClaim)?.Value}{JwtTenantProvider.KeySeparator}{ctx.User.FindFirst("sub")?.Value ?? ctx.User.Identity?.Name}";

    o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(UserKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = config.GetValue("RateLimit:ChatPerMinute", 30),
        Window = TimeSpan.FromMinutes(1)
    }));

    o.AddPolicy(AttachmentEndpointRouteBuilderExtensions.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        UserKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimit:AttachmentsPerMinute", 30),
            Window = TimeSpan.FromMinutes(1)
        }));

    // Minting is cheap but unauthenticated-adjacent work; reconnect storms after a deploy need headroom.
    o.AddPolicy(TicketEndpointRouteBuilderExtensions.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        UserKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimit:TicketPerMinute", 30),
            Window = TimeSpan.FromMinutes(1)
        }));
});

var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().WithMethods("POST", "GET", "DELETE", "OPTIONS")
        .WithExposedHeaders("Retry-After")));

builder.AddHttpHardening();
var app = builder.Build();
app.ValidateConfigurationAtStartup();

// First, so the scheme and client address seen by everything below come from a trusted proxy only.
app.UseForwardedHeadersFirst();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionStatusCodeHandler>();
// Logs the path only, never the query string (the hub ticket travels there). It is given this host's own logger.
app.UseSerilogRequestLogging(o => o.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
// Inside the exception handler so an oversized body becomes a 413 problem response.
app.UseHttpHardening();
app.Use(async (ctx, next) =>
{
    // The hub URL can carry a ticket; keep it out of caches, and out of the Referer header sent on to other sites.
    if (ctx.Request.Path.StartsWithSegments(ChatHub.Path))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next();
});
// Public static files are served before authentication; the fallback policy would otherwise demand a token.
var spaFound = app.UseSpaStaticFiles();
// Explicit so static files are served before endpoint selection; otherwise the file fallback would shadow them.
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    // Tenant goes on the trace, not on metrics (unbounded cardinality).
    if (ctx.User.FindFirst(JwtTenantProvider.TenantClaim)?.Value is { } tenant)
    {
        Activity.Current?.SetTag("helpdesk.tenant_id", tenant);
    }
    await next();
});
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/readyz", (ChatJobProcessor processor) =>
    processor.IsRunning
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();
app.MapGet("/status", (ConfigurationValidator validator) =>
{
    var report = validator.Validate();
    // 503 on any failed check so a deployment gate or probe can act on the status code alone.
    return Results.Json(report, statusCode: report.Status == "invalid" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
}).RequireAuthorization(StatusPolicy);
app.MapChatEndpoints();
app.MapAttachmentEndpoints();
// Tickets only exist when the hub is authenticated with them; Development dev auth does not use them.
if (!useDevAuth) app.MapTicketEndpoint();
// The fallback policy (authenticated + tenant claim) applies to the negotiate request. Close connections when
// their token expires so a long-lived socket cannot outlive the credential that opened it.
app.MapHub<ChatHub>(ChatHub.Path, o => o.CloseOnAuthenticationExpiration = true);
app.MapSpaFallback(spaFound);

app.Run();

public partial class Program
{
    internal const string StatusPolicy = "status";
    internal const string DefaultStatusRole = "Helpdesk.Admin";
    internal const string RoutingScheme = "Helpdesk";
}
