using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AgentDesk.Api;
using AgentDesk.Api.Endpoints;
using AgentDesk.Api.Observability;
using AgentDesk.Api.Realtime;
using AgentDesk.Api.Security;
using AgentDesk.Core.Abstractions;
using AgentDesk.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.AddAgentDeskObservability();

var config = builder.Configuration;
var services = builder.Services;
var authOptions = config.GetSection(AgentDeskAuthOptions.SectionName).Get<AgentDeskAuthOptions>() ?? new();
var authMode = builder.Environment.IsDevelopment() && authOptions.UseDevAuth ? AuthModes.Dev
    : authOptions.Enforce ? AuthModes.Jwt
    : AuthModes.Anonymous;

services.Configure<AgentDeskAuthOptions>(config.GetSection(AgentDeskAuthOptions.SectionName));
services.Configure<HubTicketOptions>(config.GetSection(HubTicketOptions.SectionName));
services.TryAddSingleton(TimeProvider.System);
services.AddSingleton<IHubTicketStore, InMemoryHubTicketStore>();
services.AddHttpContextAccessor();
services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();

var auth = authMode switch
{
    AuthModes.Dev => services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null),
    AuthModes.Anonymous => services.AddAuthentication(AnonymousAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, AnonymousAuthHandler>(AnonymousAuthHandler.SchemeName, null),
    _ => services
        .AddAuthentication(o =>
        {
            o.DefaultScheme = Program.RoutingScheme;
            o.DefaultChallengeScheme = Program.RoutingScheme;
        })
        // Browsers cannot put a bearer header on a WebSocket and the hub URL lands in proxy logs, so the hub takes a
        // short-lived single-use ticket bought with the JWT from POST /api/v1/hub/ticket. A raw JWT on the hub is refused.
        .AddPolicyScheme(Program.RoutingScheme, null, o =>
            o.ForwardDefaultSelector = ctx => ctx.Request.Path.StartsWithSegments(TicketsHub.Path)
                ? HubTicketAuthHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme)
        .AddScheme<AuthenticationSchemeOptions, HubTicketAuthHandler>(HubTicketAuthHandler.SchemeName, null)
        .AddJwtBearer(o =>
        {
            o.Authority = authOptions.Authority;
            o.Audience = authOptions.Audience;
            o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = AgentDeskClaims.RoleClaim;
        })
};

services.AddAuthorization(o =>
{
    // Every caller needs a tenant and a user id usable in partition keys; there is no tenant-less access.
    o.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => AgentDeskClaims.HasValidIdentity(ctx.User))
        .Build();

    static AuthorizationPolicy RolePolicy(params string[] roles) => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => AgentDeskClaims.HasValidIdentity(ctx.User)
            && roles.Append(AgentDeskRoles.Admin).Any(r => HasRole(ctx.User, r)))
        .Build();

    o.AddPolicy(AgentDeskPolicies.Agent, RolePolicy(AgentDeskRoles.Agent, AgentDeskRoles.KbAuthor));
    o.AddPolicy(AgentDeskPolicies.KbAuthor, RolePolicy(AgentDeskRoles.KbAuthor));
    o.AddPolicy(AgentDeskPolicies.Admin, RolePolicy());
});

static bool HasRole(ClaimsPrincipal user, string role) =>
    user.IsInRole(role) || user.HasClaim(AgentDeskClaims.RoleClaim, role) || user.HasClaim(ClaimTypes.Role, role);

services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
// Registered before the infrastructure, which only falls back to a no-op notifier.
services.AddSingleton<ITicketNotifier, SignalRTicketNotifier>();
services.AddAgentDeskInfrastructure(config);

services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
services.AddProblemDetails();

services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string UserKey(HttpContext ctx) =>
        $"{AgentDeskClaims.TenantId(ctx.User)}:{AgentDeskClaims.UserId(ctx.User) ?? ctx.Connection.RemoteIpAddress?.ToString()}";

    o.AddPolicy(AssistEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(UserKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = config.GetValue("RateLimit:AssistPerMinute", 20),
        Window = TimeSpan.FromMinutes(1)
    }));
    o.AddPolicy(Program.HubTicketRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(UserKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = config.GetValue("RateLimit:HubTicketPerMinute", 30),
        Window = TimeSpan.FromMinutes(1)
    }));
});

var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
    .WithExposedHeaders("ETag", CorrelationIdMiddleware.HeaderName)
    .AllowCredentials()));

var app = builder.Build();

if (authMode == AuthModes.Anonymous)
{
    app.Logger.LogWarning(
        "Authentication is not enforced (Auth:Enforce=false). Every caller acts as {User} in tenant {Tenant}. Do not expose this deployment publicly.",
        authOptions.Anonymous.UserId, authOptions.Anonymous.TenantId);
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionStatusCodeHandler>();
app.UseSerilogRequestLogging(o => o.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.Use(async (ctx, next) =>
{
    // Hub and dev-blob URLs can carry a ticket or signature; keep them out of caches and Referer headers.
    if (ctx.Request.Path.StartsWithSegments(TicketsHub.Path) || ctx.Request.Path.StartsWithSegments("/dev-blobs"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next();
});
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    if (AgentDeskClaims.TenantId(ctx.User) is { } tenant) Activity.Current?.SetTag("agentdesk.tenant_id", tenant);
    await next();
});
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" })).AllowAnonymous();
app.MapGet("/status", (IConfiguration cfg, AgentDesk.Infrastructure.Ai.TicketAssistService assist) => Results.Ok(new
{
    authMode,
    storage = cfg["Storage:Provider"] ?? "InMemory",
    blobs = cfg["Blobs:Provider"] ?? "InMemory",
    intake = cfg["Intake:Provider"] ?? "None",
    assist = assist.Describe()
})).RequireAuthorization(AgentDeskPolicies.Admin);

var api = app.MapGroup("/api/v1");
api.MapTicketEndpoints();
api.MapKbEndpoints();
api.MapAssistEndpoints();
api.MapMetaEndpoints(authMode);

if (authMode == AuthModes.Jwt)
{
    api.MapPost("/hub/ticket", async (HttpContext ctx, IHubTicketStore store) =>
    {
        var user = Http.User(ctx);
        var result = await ctx.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        var expires = result.Properties?.ExpiresUtc
            ?? (long.TryParse(ctx.User.FindFirst("exp")?.Value, out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp) : DateTimeOffset.UtcNow.AddMinutes(5));
        var ticket = store.Issue(new HubTicketClaims(user.TenantId, user.UserId, user.DisplayName, expires));
        return ticket is null
            ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
            : Results.Ok(new { ticket = ticket.Value, expiresInSeconds = (int)ticket.Lifetime.TotalSeconds });
    }).RequireRateLimiting(Program.HubTicketRateLimitPolicy).WithTags("Realtime");
}

app.MapDevBlobEndpoints();
app.MapHub<TicketsHub>(TicketsHub.Path, o => o.CloseOnAuthenticationExpiration = true);

app.Run();

public partial class Program
{
    internal const string RoutingScheme = "AgentDesk";
    internal const string HubTicketRateLimitPolicy = "hub-ticket";
}
