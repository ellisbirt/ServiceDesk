using Microsoft.Playwright;
using ServiceDesk.UiTests.AgentDeskUi;
using ServiceDesk.UiTests.HelpdeskUi;

namespace ServiceDesk.UiTests.Infrastructure;

/// <summary>
/// Everything that is expensive and shared by all the UI tests: Playwright, the browser, and (started the first time a test
/// asks for them) the helpdesk app and the AgentDesk front end. It is created once. Each test gets its own browser context
/// (its own cookies and storage) from <see cref="UiTestBase"/>.
/// </summary>
public sealed class UiFixture : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private readonly Lazy<Task<HelpdeskApp>> _helpdesk;
    private readonly Lazy<Task<StaticSpaHost>> _agentDesk;

    public UiFixture()
    {
        _helpdesk = new(() => Task.Run(() =>
        {
            var app = new HelpdeskApp(SpaBuild.Ensure(SpaApp.Helpdesk));
            _ = app.Services; // starts the host, which also starts the Kestrel server the browser connects to
            return app;
        }));
        _agentDesk = new(() => Task.Run(() => new StaticSpaHost(SpaBuild.Ensure(SpaApp.AgentDesk))));
    }

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>The real helpdesk API, serving the built front end, with the model, search and queue scripted.</summary>
    public Task<HelpdeskApp> HelpdeskAsync() => _helpdesk.Value;

    /// <summary>The built AgentDesk front end on a loopback port. Its API is mocked in the browser by each test.</summary>
    public Task<StaticSpaHost> AgentDeskAsync() => _agentDesk.Value;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Assertions.SetDefaultExpectTimeout((float)PageObject.Timeout.TotalMilliseconds);
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = await Task.Run(BrowserLocator.Find),
            Args = ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--hide-scrollbars"]
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_helpdesk.IsValueCreated) (await _helpdesk.Value).Dispose();
        if (_agentDesk.IsValueCreated) await (await _agentDesk.Value).DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<UiFixture>
{
    public const string Name = "UI";
}

/// <summary>
/// Base class of the UI tests: a fresh browser context and page for every test, with the page's errors collected. The
/// derived classes open the app they test.
/// </summary>
[Collection(UiCollection.Name)]
public abstract class UiTestBase(UiFixture fixture) : IAsyncLifetime
{
    private IBrowserContext _context = null!;
    private readonly List<string> _tempFiles = [];

    protected UiFixture Fixture => fixture;
    protected IPage Page { get; private set; } = null!;

    /// <summary>Errors the page logged to its console or threw, for tests that care about a clean run.</summary>
    protected List<string> BrowserErrors { get; } = [];

    public virtual async Task InitializeAsync()
    {
        _context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        Page = await _context.NewPageAsync();
        Page.PageError += (_, message) => BrowserErrors.Add("page error: " + message);
        Page.Console += (_, e) =>
        {
            // "Failed to load resource" carries no URL; the response handler below reports those properly.
            if (e.Type == "error" && !e.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                BrowserErrors.Add("console: " + e.Text);
        };
        // The console only says "Failed to load resource"; this says which one.
        Page.Response += (_, e) =>
        {
            // The apps have no favicon, so every page load asks for one and gets a 404. Not worth failing a test over.
            if (e.Status >= 400 && !e.Url.EndsWith("/favicon.ico", StringComparison.Ordinal))
                BrowserErrors.Add($"response: {e.Status} {e.Request.Method} {e.Url}");
        };
    }

    public virtual async Task DisposeAsync()
    {
        await _context.CloseAsync();
        foreach (var file in _tempFiles.Where(File.Exists)) File.Delete(file);
    }

    /// <summary>Writes a file the browser can choose, and removes it when the test ends.</summary>
    protected string TempFile(string name, byte[] content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "servicedesk-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    protected string TempFile(string name, string content) => TempFile(name, System.Text.Encoding.UTF8.GetBytes(content));
}
