using Helpdesk.UiTests.Pages;
using PuppeteerSharp;

namespace Helpdesk.UiTests.Infrastructure;

/// <summary>
/// Everything that is expensive and shared by all the UI tests: the built front end, the running app, and the browser.
/// It is started once. Each test gets its own browser context (its own cookies and storage) from <see cref="UiTest"/>.
/// </summary>
public sealed class UiFixture : IAsyncLifetime
{
    public HelpdeskApp App { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var webRoot = await Task.Run(() => SpaBuild.Ensure());
        App = new HelpdeskApp(webRoot);
        _ = App.Services; // starts the host, which also starts the Kestrel server the browser connects to

        Browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            ExecutablePath = await BrowserLocator.FindAsync(),
            Args = ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--hide-scrollbars"]
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        App?.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<UiFixture>
{
    public const string Name = "UI";
}

/// <summary>
/// Base class of the UI tests: a fresh browser context and page for every test, the app's recorded state cleared, and
/// the chat page open and ready.
/// </summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
public abstract class UiTest(UiFixture fixture) : IAsyncLifetime
{
    private IBrowserContext _context = null!;
    private readonly List<string> _tempFiles = [];

    protected HelpdeskApp App => fixture.App;
    protected IPage Page { get; private set; } = null!;
    protected ChatPage Chat { get; private set; } = null!;

    /// <summary>Errors the page logged to its console or threw, for tests that care about a clean run.</summary>
    protected List<string> BrowserErrors { get; } = [];

    public async Task InitializeAsync()
    {
        App.Reset();
        _context = await fixture.Browser.CreateBrowserContextAsync();
        Page = await _context.NewPageAsync();
        Page.PageError += (_, e) => BrowserErrors.Add("page error: " + e.Message);
        Page.Console += (_, e) =>
        {
            // "Failed to load resource" carries no URL; the response handler below reports those properly.
            if (e.Message.Type == ConsoleType.Error && !e.Message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                BrowserErrors.Add("console: " + e.Message.Text);
        };
        // The console only says "Failed to load resource"; this says which one.
        Page.Response += (_, e) =>
        {
            // The app has no favicon, so every page load asks for one and gets a 404. Not worth failing a test over.
            if ((int)e.Response.Status >= 400 && !e.Response.Url.EndsWith("/favicon.ico", StringComparison.Ordinal)) BrowserErrors.Add($"response: {(int)e.Response.Status} {e.Response.Request.Method} {e.Response.Url}");
        };
        Chat = await ChatPage.OpenAsync(Page, App.Address);
    }

    public async Task DisposeAsync()
    {
        await _context.CloseAsync();
        foreach (var file in _tempFiles.Where(File.Exists)) File.Delete(file);
    }

    /// <summary>Writes a file the browser can choose, and removes it when the test ends.</summary>
    protected string TempFile(string name, byte[] content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "helpdesk-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    protected string TempFile(string name, string content) => TempFile(name, System.Text.Encoding.UTF8.GetBytes(content));
}
