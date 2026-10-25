using PuppeteerSharp;

namespace Helpdesk.UiTests.Infrastructure;

/// <summary>
/// Finds a Chromium-based browser to drive. In order: <c>HELPDESK_UI_BROWSER</c> (a path to chrome or msedge), an installed
/// Chrome or Edge, and only then a download of Chromium (about 150 MB, once, into the user's cache).
/// </summary>
public static class BrowserLocator
{
    public static async Task<string> FindAsync()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HELPDESK_UI_BROWSER");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return File.Exists(fromEnvironment)
                ? fromEnvironment
                : throw new InvalidOperationException($"HELPDESK_UI_BROWSER ({fromEnvironment}) does not exist.");
        }

        var installed = Candidates().FirstOrDefault(File.Exists);
        if (installed is not null) return installed;

        var fetched = await new BrowserFetcher().DownloadAsync();
        return fetched.GetExecutablePath();
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Environment.GetEnvironmentVariable("LocalAppData")
                     }.Where(r => !string.IsNullOrEmpty(r)))
            {
                yield return Path.Combine(root!, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root!, "Microsoft", "Edge", "Application", "msedge.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        }
        else
        {
            foreach (var path in new[] { "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge" })
                yield return path;
        }
    }
}
