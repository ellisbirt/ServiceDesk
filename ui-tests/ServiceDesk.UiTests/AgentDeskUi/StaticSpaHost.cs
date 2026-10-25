using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ServiceDesk.UiTests.AgentDeskUi;

/// <summary>
/// Serves a built front end from a folder on a free loopback port, with unknown paths falling back to <c>index.html</c> (the app
/// does its own routing). It has no API: a test answers the app's <c>/api/v1</c> requests in the browser.
/// </summary>
public sealed class StaticSpaHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public StaticSpaHost(string webRoot)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot, ContentRootPath = webRoot });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        var files = new PhysicalFileProvider(webRoot);
        _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        _app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        _app.MapFallback(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
        });
        _app.Start();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Address = new Uri(addresses.First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1"));
    }

    /// <summary>Where the browser goes, for example <c>http://127.0.0.1:53211/</c>.</summary>
    public Uri Address { get; }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
