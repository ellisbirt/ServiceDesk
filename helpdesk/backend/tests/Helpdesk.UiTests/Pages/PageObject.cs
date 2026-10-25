using PuppeteerSharp;

namespace Helpdesk.UiTests.Pages;

/// <summary>
/// Base class of the page objects. A page object is the only place that knows how the screen is built (selectors, waits,
/// scripts); tests describe what a person does and sees. If the markup changes, one class changes and the tests do not.
/// </summary>
public abstract class PageObject(IPage page)
{
    /// <summary>How long to wait for the screen to catch up before a test fails. Override with HELPDESK_UI_TIMEOUT_SECONDS.</summary>
    public static TimeSpan Timeout { get; } =
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("HELPDESK_UI_TIMEOUT_SECONDS"), out var s) ? s : 15);

    protected IPage Page { get; } = page;

    /// <summary>Polls until <paramref name="condition"/> is true, or fails with <paramref name="description"/> and what was last seen.</summary>
    protected async Task WaitUntilAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        var started = DateTime.UtcNow;
        while (true)
        {
            if (await condition()) return;
            if (DateTime.UtcNow - started > limit)
                throw new TimeoutException($"Gave up after {limit.TotalSeconds:0}s waiting for {description}.");
            await Task.Delay(50);
        }
    }

    protected Task<T> EvaluateAsync<T>(string script, params object[] args) => Page.EvaluateFunctionAsync<T>(script, args);

    protected Task<bool> ExistsAsync(string selector) =>
        EvaluateAsync<bool>("sel => document.querySelector(sel) !== null", selector);

    protected Task<int> CountAsync(string selector) =>
        EvaluateAsync<int>("sel => document.querySelectorAll(sel).length", selector);

    protected Task<string> TextOfAsync(string selector) =>
        EvaluateAsync<string>("sel => (document.querySelector(sel)?.textContent ?? '').trim()", selector);

    protected Task WaitForVisibleAsync(string selector) =>
        Page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Visible = true, Timeout = (int)Timeout.TotalMilliseconds });

    /// <summary>Clicks the first button inside <paramref name="container"/> whose text is exactly <paramref name="text"/>.</summary>
    protected async Task ClickButtonAsync(string container, string text)
    {
        var clicked = await EvaluateAsync<bool>(
            """
            (container, text) => {
              const button = [...document.querySelectorAll(container + ' button')].find(b => b.textContent.trim() === text);
              if (!button || button.disabled) return false;
              button.click();
              return true;
            }
            """, container, text);
        if (!clicked) throw new InvalidOperationException($"There is no enabled '{text}' button in '{container}'.");
    }
}
