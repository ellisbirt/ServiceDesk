using ServiceDesk.UiTests.Infrastructure;

namespace ServiceDesk.UiTests.HelpdeskUi;

public sealed class ChatTests(UiFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task TheApp_OpensOnAnEmptyConversation()
    {
        Assert.Equal("IT Helpdesk Assistant", await Chat.HeadingAsync());
        Assert.Contains("Ask a question about your devices", await Chat.EmptyStateAsync());
        Assert.Empty(await Chat.MessagesAsync());
        Assert.False(await Chat.CanSendAsync()); // nothing to send yet
        Assert.Empty(BrowserErrors);
    }

    [Fact]
    public async Task TheServiceStatus_SaysWhenTheApiIsReady()
    {
        await Task.Delay(0);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await Chat.ServiceStatusAsync() != "Service ready" && DateTime.UtcNow < deadline) await Task.Delay(100);

        Assert.Equal("Service ready", await Chat.ServiceStatusAsync());
    }

    [Fact]
    public async Task AQuestion_IsAnswered_AndShownInTheConversation()
    {
        var reply = await Chat.AskAsync("My printer shows a paper jam but nothing is stuck.");

        var messages = await Chat.MessagesAsync();
        Assert.Equal(["user", "assistant"], messages.Select(m => m.Kind));
        Assert.Equal("My printer shows a paper jam but nothing is stuck.", messages[0].Text);
        Assert.Equal("PUBLIC_TECH", reply.Lane);
        Assert.Equal("General tech", reply.LaneLabel);
        Assert.Contains("restart the app", reply.Text);
        Assert.Equal("", await Chat.MessageBoxAsync()); // the box is cleared once the message is sent
        Assert.Empty(BrowserErrors);
    }

    [Fact]
    public async Task TheSendButton_IsOnlyEnabledWhenThereIsSomethingToSend()
    {
        Assert.False(await Chat.CanSendAsync());

        await Chat.FillAsync("hello");
        Assert.True(await Chat.CanSendAsync());

        await Chat.FillAsync("   ");
        Assert.False(await Chat.CanSendAsync());
    }

    [Fact]
    public async Task TheAnswer_ArrivesInPieces_BeforeItIsComplete()
    {
        App.Inference.Pause = TimeSpan.FromMilliseconds(400);

        await Chat.FillAsync("How do I fix my Wi-Fi?");
        await Chat.ClickSendAsync();
        var partial = await Chat.WaitForLiveReplyAsync();
        var final = await Chat.WaitForReplyAsync(1);

        Assert.Equal("live", partial.Kind);
        Assert.True(partial.Text.Length < final.Text.Length, "the live text should be shorter than the finished reply");
        Assert.Contains("Here is how to fix that", final.Text);
        Assert.DoesNotContain(await Chat.MessagesAsync(), m => m.Kind == "live"); // the finished bubble replaced it
    }

    [Fact]
    public async Task Enter_Sends_AndShiftEnter_AddsALine()
    {
        await Chat.TypeAsync("first line");
        await Chat.PressShiftEnterAsync();
        await Chat.TypeAsync("second line");
        Assert.Equal("first line\nsecond line", await Chat.MessageBoxAsync());

        await Chat.PressEnterAsync();
        var reply = await Chat.WaitForReplyAsync(1);

        Assert.Equal("first line\nsecond line", (await Chat.MessagesAsync())[0].Text);
        Assert.Equal("PUBLIC_TECH", reply.Lane);
    }

    [Fact]
    public async Task AFollowUp_CarriesTheConversationSoFar()
    {
        await Chat.AskAsync("My laptop is slow.");
        await Chat.AskAsync("It is still slow after a restart.");

        var calls = App.Inference.Calls.ToArray();
        Assert.Equal([0, 2], calls.Select(c => c.HistoryMessages)); // the second question sees the first exchange
        Assert.Equal(["user", "assistant", "user", "assistant"], (await Chat.MessagesAsync()).Select(m => m.Kind));
    }

    [Fact]
    public async Task ANewConversation_StartsAgainFromNothing()
    {
        await Chat.AskAsync("My laptop is slow.");

        await Chat.NewConversationAsync();
        Assert.Empty(await Chat.MessagesAsync());
        await Chat.AskAsync("Different question about email.");

        Assert.Equal(0, App.Inference.Calls.Last().HistoryMessages);
        Assert.Single(await Chat.RepliesAsync());
    }

    [Fact]
    public async Task Markdown_IsShownAsFormatting_ButMarkupInAnAnswerIsNeverRun()
    {
        await Chat.AskAsync("Show me markdown please.");

        var html = await Page.EvaluateAsync<string>("() => document.querySelector('[data-testid=\"message-body\"]').innerHTML");
        Assert.Contains("<strong>Bold step</strong>", html);
        Assert.Contains("<code>ipconfig /all</code>", html);
        Assert.Equal(2, Regex.Matches(html, "<li>").Count);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(await Page.EvaluateAsync<bool>("() => window.__injected === true"));
    }

    [Fact]
    public async Task SomethingUnrelated_IsDeclinedPolitely()
    {
        var reply = await Chat.AskAsync("Tell me a joke.");

        Assert.Equal("NO_MATCH", reply.Lane);
        Assert.Equal("Out of scope", reply.LaneLabel);
        Assert.Contains("I can help with general technical questions", reply.Text);
    }

    [Fact]
    public async Task AFailure_IsShownAsAnError_AndTheAppStaysUsable()
    {
        var failure = await Chat.AskAsync("Please explode.");

        Assert.True(failure.IsError);
        Assert.Contains("could not be safely classified", failure.Text, StringComparison.OrdinalIgnoreCase);

        var recovered = await Chat.AskAsync("My laptop is slow.");
        Assert.Equal("PUBLIC_TECH", recovered.Lane);
    }

    [Fact]
    public async Task LiveStatusQuestions_AreAnsweredFromSearch()
    {
        var reply = await Chat.AskAsync("Is Azure down? Any outage today?");

        Assert.Equal("PUBLIC_SEARCH", reply.Lane);
        Assert.Equal("Live search", reply.LaneLabel);
        Assert.Contains("all services are operating normally", reply.Text);
    }
}
