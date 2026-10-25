using System.Net;
using Helpdesk.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Helpdesk.UiTests.Infrastructure;

/// <summary>
/// The real helpdesk API and the built front end, listening on a real loopback port so a browser can use them. The model,
/// search and ticket queue are replaced by <see cref="ScriptedServices"/>-style stand-ins that the tests can inspect.
/// Everything else (the pipeline, the SignalR hub, uploads, attachment checks, dev auth) is the real code.
/// </summary>
public sealed class HelpdeskApp : WebApplicationFactory<Program>
{
    private readonly string _webRoot;
    private IHost? _kestrel;

    public ScriptedRouter Router { get; } = new();
    public ScriptedInference Inference { get; } = new();
    public ScriptedInterviewer Interviewer { get; } = new();
    public RecordingPublisher Publisher { get; } = new();

    public HelpdeskApp(string webRoot) => _webRoot = webRoot;

    /// <summary>Where the browser goes, for example <c>http://127.0.0.1:53211/</c>.</summary>
    public Uri Address { get; private set; } = new("http://127.0.0.1");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseWebRoot(_webRoot);
        builder.UseSetting("Auth:UseDevAuth", "true");
        // Many tests share one user; the real limits are not what is under test here.
        builder.UseSetting("RateLimit:ChatPerMinute", "1000");
        builder.UseSetting("RateLimit:AttachmentsPerMinute", "1000");
        builder.UseSetting("Chat:HeartbeatSeconds", "1");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGuardrailRouterService>();
            services.AddSingleton<IGuardrailRouterService>(Router);
            services.RemoveAll<IPublicInferenceService>();
            services.AddSingleton<IPublicInferenceService>(Inference);
            services.RemoveAll<ITicketInterviewer>();
            services.AddSingleton<ITicketInterviewer>(Interviewer);
            services.RemoveAll<ITicketQueuePublisher>();
            services.AddSingleton<ITicketQueuePublisher>(Publisher);
            services.RemoveAll<ILiveSearchService>();
            services.AddSingleton<ILiveSearchService>(new StubSearch());
            services.RemoveAll<IKnowledgeBaseSearch>();
            services.AddSingleton<IKnowledgeBaseSearch>(new NoKnowledgeBase());
        });
    }

    /// <summary>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> serves from an in-memory test server, which a browser cannot reach.
    /// So this also starts the same app on Kestrel, on a free loopback port, and the browser uses that one. The in-memory
    /// host is still built because the base class insists on it.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var testHost = builder.Build();

        builder.ConfigureWebHost(web => web.UseKestrel().UseUrls("http://127.0.0.1:0"));
        _kestrel = builder.Build();
        _kestrel.Start();

        var addresses = _kestrel.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Address = new Uri(addresses.First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1"));

        testHost.Start();
        return testHost;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _kestrel?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Gets ready for the next test: forgets what the stand-ins recorded.</summary>
    public void Reset()
    {
        Router.Seen.Clear();
        Inference.Calls.Clear();
        Interviewer.Conversations.Clear();
        Publisher.Sent.Clear();
        Inference.Pause = TimeSpan.FromMilliseconds(60);
    }
}
