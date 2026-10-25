using ServiceDesk.UiTests.Infrastructure;

namespace ServiceDesk.UiTests.HelpdeskUi;

/// <summary>
/// axe-core checks (WCAG 2.0/2.1 A and AA) on the screens a person actually sees, in a real browser: the empty chat, an answer, an
/// attached file and an error. The component tests in the front end's own suite cover the same rules in a simulated DOM.
/// </summary>
public sealed class AccessibilityTests(UiFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task TheEmptyChat_IsAccessible_AndSaysTheServiceIsReady()
    {
        Assert.Contains("Service ready", await Chat.ServiceStatusAsync());

        await Accessibility.ExpectNoViolationsAsync(Page);
    }

    [Fact]
    public async Task AnAnswer_IsAccessible()
    {
        await Chat.AskAsync("My laptop will not turn on");

        await Accessibility.ExpectNoViolationsAsync(Page);
    }

    [Fact]
    public async Task AnAttachedFile_IsAccessible_BeforeAndAfterItIsSent()
    {
        await Chat.AttachAsync(TempFile("app.log", "hello world!"));
        await Chat.Attachments.WaitForUploadedAsync("app.log");
        await Accessibility.ExpectNoViolationsAsync(Page);

        await Chat.AskAsync("What is wrong?");

        await Accessibility.ExpectNoViolationsAsync(Page);
    }

    [Fact]
    public async Task AnError_IsAccessible_AndDoesNotExposeDetails()
    {
        var failure = await Chat.AskAsync("Please explode.");

        Assert.True(failure.IsError);
        Assert.DoesNotContain("GuardrailClassificationException", failure.Text);
        Assert.DoesNotContain("scripted failure", failure.Text);
        await Accessibility.ExpectNoViolationsAsync(Page);
    }

    [Fact]
    public async Task ATicketDraft_AndItsButtons_AreAccessible()
    {
        await Chat.AskAsync("My VPN is down.");
        await Chat.AskAsync("The Contoso client.");

        await Accessibility.ExpectNoViolationsAsync(Page);
    }
}
