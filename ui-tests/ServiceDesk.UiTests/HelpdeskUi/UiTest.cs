using ServiceDesk.UiTests.Infrastructure;

namespace ServiceDesk.UiTests.HelpdeskUi;

/// <summary>
/// Base class of the helpdesk UI tests: the helpdesk app's recorded state cleared, and the chat page open and ready.
/// </summary>
public abstract class UiTest(UiFixture fixture) : UiTestBase(fixture)
{
    protected HelpdeskApp App { get; private set; } = null!;
    protected ChatPage Chat { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        App = await Fixture.HelpdeskAsync();
        App.Reset();
        Chat = await ChatPage.OpenAsync(Page, App.Address);
    }
}
