using System.Text.Json.Nodes;
using Microsoft.Playwright;
using ServiceDesk.UiTests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace ServiceDesk.UiTests.AgentDeskUi;

/// <summary>
/// The AgentDesk console, in a real browser against a mocked API (see <see cref="AgentDeskApiMock"/>), with axe-core checks on
/// each screen of the walk.
/// </summary>
public sealed class AgentConsoleTests(UiFixture fixture) : UiTestBase(fixture)
{
    private AgentDeskApiMock _api = null!;
    private StaticSpaHost _host = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _host = await Fixture.AgentDeskAsync();
        _api = new AgentDeskApiMock();
        await _api.RegisterAsync(Page);
    }

    private ILocator Heading(int level, string name) => Page.GetByRole(AriaRole.Heading, new() { Level = level, Name = name });

    [Fact]
    public async Task AnAgent_AnswersATicketWithANewKbArticle_AndFindsItInTheKnowledgeBase()
    {
        await Page.GotoAsync(new Uri(_host.Address, "tickets").ToString());
        await Expect(Heading(1, "Tickets")).ToBeVisibleAsync();
        await Accessibility.ExpectNoViolationsAsync(Page);

        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("VPN drops every few minutes") }).ClickAsync();
        await Expect(Heading(1, "VPN drops every few minutes")).ToBeVisibleAsync();
        await Expect(Page.GetByText("We are looking into it.")).ToBeVisibleAsync();
        await Accessibility.ExpectNoViolationsAsync(Page);

        var tools = Page.GetByRole(AriaRole.Complementary, new() { Name = "Ticket tools" });
        await tools.GetByRole(AriaRole.Button, new() { Name = "Reply with new KB article" }).ClickAsync();
        var review = Page.GetByRole(AriaRole.Group, new() { Name = "Review article and reply" });
        await Expect(review.GetByLabel("Title")).ToHaveValueAsync("Fix VPN disconnects");
        await Accessibility.ExpectNoViolationsAsync(Page);
        await review.GetByRole(AriaRole.Button, new() { Name = "Save article & send reply" }).ClickAsync();

        await Expect(Page.GetByText("KB-1001 saved and published, reply sent.")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Hi Alex, the steps in KB-1001 — Fix VPN disconnects should fix this.")).ToBeVisibleAsync();
        var sent = _api.Posts.Single(p => p.Path == "/tickets/t-1/comments").Body!;
        Assert.Equal("public", sent["visibility"]!.GetValue<string>());
        Assert.Equal(["kb-1"], sent["citedKbArticleIds"]!.AsArray().Select(n => n!.GetValue<string>()));

        await Page.GetByRole(AriaRole.Link, new() { Name = "Knowledge base" }).First.ClickAsync();
        await Expect(Heading(1, "Knowledge base")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Fix VPN disconnects") }).ClickAsync();
        await Expect(Heading(1, "KB-1001 — Fix VPN disconnects")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Title")).ToHaveValueAsync("Fix VPN disconnects");
        await Accessibility.ExpectNoViolationsAsync(Page);
    }
}
