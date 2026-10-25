using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace ServiceDesk.UiTests.Infrastructure;

/// <summary>Runs axe-core against the page as it is now and fails the test with every WCAG 2.0/2.1 A and AA violation it finds.</summary>
public static class Accessibility
{
    private static readonly string[] Tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

    public static async Task ExpectNoViolationsAsync(IPage page)
    {
        var results = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. Tags] } });
        Assert.True(results.Passes.Length > 0, "axe-core ran no rules, so a pass would mean nothing.");
        var violations = results.Violations
            .Select(v => $"{v.Id}: {v.Help} ({string.Join("; ", v.Nodes.Select(n => n.Target?.ToString()))})")
            .ToList();
        Assert.True(violations.Count == 0, "Accessibility violations:\n" + string.Join("\n", violations));
    }
}
