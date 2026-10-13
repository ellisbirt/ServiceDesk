using System.Net;
using Helpdesk.Api.Observability;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Helpdesk.Api.Hosting;

public static class HttpHardeningExtensions
{
    /// <summary>Paths served by the API rather than the SPA. These never fall back to <c>index.html</c>.</summary>
    private static readonly string[] ServerPrefixes = ["/api", "/hubs", "/status", "/healthz", "/readyz"];

    public static bool IsServerPath(PathString path) =>
        ServerPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/readyz", StringComparison.OrdinalIgnoreCase);

    public static WebApplicationBuilder AddHttpHardening(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(HttpHardeningOptions.SectionName);
        builder.Services.Configure<HttpHardeningOptions>(section);
        var options = section.Get<HttpHardeningOptions>() ?? new HttpHardeningOptions();

        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = Math.Max(1, options.ForwardedHeaders.ForwardLimit);
            // Defaults trust loopback only. Configured proxies replace them; invalid entries are reported by
            // ConfigurationValidator and skipped here so the app never trusts more than intended.
            var proxies = options.ForwardedHeaders.KnownProxies
                .Select(p => IPAddress.TryParse(p, out var ip) ? ip : null).OfType<IPAddress>().ToList();
            var networks = options.ForwardedHeaders.KnownNetworks
                .Select(n => System.Net.IPNetwork.TryParse(n, out var net) ? net : (System.Net.IPNetwork?)null)
                .OfType<System.Net.IPNetwork>().ToList();
            if (proxies.Count + networks.Count > 0)
            {
                o.KnownProxies.Clear();
                o.KnownIPNetworks.Clear();
                proxies.ForEach(o.KnownProxies.Add);
                networks.ForEach(o.KnownIPNetworks.Add);
            }
        });

        builder.Services.AddHsts(o =>
        {
            o.MaxAge = TimeSpan.FromDays(Math.Max(1, options.Hsts.MaxAgeDays));
            o.IncludeSubDomains = options.Hsts.IncludeSubDomains;
            o.Preload = options.Hsts.Preload;
        });
        builder.Services.AddHttpsRedirection(o =>
        {
            o.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;
            o.HttpsPort = options.HttpsPort;
        });
        return builder;
    }

    /// <summary>
    /// Forwarded headers, security headers, HSTS, HTTPS redirect and the request body limit. Call
    /// <see cref="UseForwardedHeadersFirst"/> before anything else that reads the scheme or client address.
    /// </summary>
    public static WebApplication UseForwardedHeadersFirst(this WebApplication app)
    {
        app.UseForwardedHeaders();
        return app;
    }

    public static WebApplication UseHttpHardening(this WebApplication app)
    {
        var options = app.Configuration.GetSection(HttpHardeningOptions.SectionName).Get<HttpHardeningOptions>() ?? new();

        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            if (options.HttpsRedirection)
            {
                // Probes come from the platform over plain HTTP inside the network; redirecting them would fail health checks.
                app.UseWhen(ctx => !IsProbe(ctx.Request.Path), b => b.UseHttpsRedirection());
            }
        }

        var limit = options.MaxRequestBodyBytes;
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Request.ContentLength > limit)
                {
                    throw new BadHttpRequestException(
                        $"Request body exceeds the {limit}-byte limit.", StatusCodes.Status413PayloadTooLarge);
                }
                // Enforces the limit for chunked bodies too (Kestrel stops reading and fails with 413).
                if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                {
                    feature.MaxRequestBodySize = limit;
                }
            }
            return next(ctx);
        });
        return app;
    }

    /// <summary>
    /// Serves the built SPA from <c>wwwroot</c> when it exists. Must run before authentication: static files are
    /// public and the authorization fallback policy would otherwise apply to them. Returns whether the SPA was found.
    /// </summary>
    public static bool UseSpaStaticFiles(this WebApplication app)
    {
        if (!app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists) return false;
        app.UseDefaultFiles();
        app.UseStaticFiles(SpaFileOptions());
        return true;
    }

    /// <summary>
    /// Client-side routes fall back to <c>index.html</c>; unknown API and hub paths return a 404 problem instead
    /// so a typo in an API call never gets HTML back.
    /// </summary>
    public static WebApplication MapSpaFallback(this WebApplication app, bool spaFound)
    {
        app.MapFallback("/api/{**rest}", NotFound).AllowAnonymous();
        app.MapFallback("/hubs/{**rest}", NotFound).AllowAnonymous();
        if (spaFound)
        {
            app.MapFallbackToFile("{*path:nonfile}", "index.html", SpaFileOptions()).AllowAnonymous();
            // Static files are public, so a missing one is a plain 404 rather than a 401 from the fallback auth policy.
            app.MapFallback("{*path:file}", static ctx => { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return Task.CompletedTask; })
                .AllowAnonymous();
        }
        return app;
    }

    private static IResult NotFound(HttpContext ctx) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Not found.",
        type: "urn:helpdesk:problem:not_found",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "not_found",
            ["correlationId"] = CorrelationIdMiddleware.Get(ctx),
        });

    private static StaticFileOptions SpaFileOptions() => new()
    {
        ContentTypeProvider = new FileExtensionContentTypeProvider(),
        OnPrepareResponse = ctx =>
        {
            // Vite fingerprints everything under /assets, so it can be cached forever; index.html must revalidate
            // so a deployment is picked up straight away.
            ctx.Context.Response.Headers[HeaderNames.CacheControl] =
                ctx.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
        },
    };
}
