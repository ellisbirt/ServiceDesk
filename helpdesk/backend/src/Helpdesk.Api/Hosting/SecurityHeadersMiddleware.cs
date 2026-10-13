using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Hosting;

/// <summary>
/// Adds browser security headers to every response, including error responses written later in the pipeline
/// (headers are added when the response starts, after any <c>Response.Clear()</c>). API responses are never
/// cached and may not load anything; the SPA document gets a policy that only allows this origin and the
/// sign-in authority.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<HttpHardeningOptions> options, IConfiguration config)
{
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    private readonly string _spaPolicy = options.Value.SpaContentSecurityPolicy(config);

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
            headers.TryAdd("Permissions-Policy", PermissionsPolicy);
            headers.TryAdd("Cross-Origin-Opener-Policy", "same-origin");

            if (HttpHardeningExtensions.IsServerPath(context.Request.Path))
            {
                headers.TryAdd("Cache-Control", "no-store");
                headers.TryAdd("Content-Security-Policy", ApiContentSecurityPolicy);
            }
            else
            {
                headers.TryAdd("Content-Security-Policy", _spaPolicy);
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}
