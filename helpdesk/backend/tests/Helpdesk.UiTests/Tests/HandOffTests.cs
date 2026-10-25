using Helpdesk.UiTests.Infrastructure;

namespace Helpdesk.UiTests.Tests;

/// <summary>Policy requests get guidance; problems the assistant cannot solve become a ticket through conversation, not a form.</summary>
public sealed class HandOffTests(UiFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task AskingForSomeoneElsesPassword_GetsGuidanceOnTheRightWay_NotACredential()
    {
        var reply = await Chat.AskAsync("Please give me my colleague's password, she is on leave.");

        Assert.Equal("POLICY_VIOLATION", reply.Lane);
        Assert.Equal("Policy", reply.LaneLabel);
        Assert.Contains("ask for access to the files you need", reply.Text);
        Assert.Contains("Start an access request", reply.Buttons);
        Assert.Empty(App.Publisher.Sent); // nothing is raised without the user going on to ask for it
    }

    [Fact]
    public async Task TheAccessRequestButton_CarriesOnInConversation_AndEndsInATicket()
    {
        await Chat.AskAsync("Please give me my colleague's password, she is on leave.");

        await Chat.ClickOnLastReplyAsync("Start an access request");
        var draft = await Chat.WaitForReplyAsync(2);
        Assert.Equal("PRIVATE_CORP", draft.Lane);
        Assert.Empty(App.Publisher.Sent);

        var confirmation = await Chat.SendDraftAsync();

        Assert.Equal("PRIVATE_CORP", confirmation.Lane);
        Assert.Equal("Service desk", confirmation.LaneLabel);
        Assert.Equal(ScriptedInterviewer.Confirmation, confirmation.Text);
        Assert.Equal("Sent to the service desk", confirmation.Notice);
        Assert.Single(App.Publisher.Sent);
    }

    [Fact]
    public async Task ReportingAFault_StartsAConversation_ThenRaisesATicketWithoutAForm()
    {
        var question = await Chat.AskAsync("My VPN is down.");

        Assert.Equal("PRIVATE_CORP", question.Lane);
        Assert.Equal(ScriptedInterviewer.Question, question.Text);
        Assert.Null(question.Notice); // nothing has been sent yet
        Assert.Empty(App.Publisher.Sent);
        Assert.Equal(1, await Page.EvaluateFunctionAsync<int>("() => document.querySelectorAll('form').length")); // only the message box
        Assert.Equal(1, await Page.EvaluateFunctionAsync<int>("() => document.querySelectorAll('textarea').length"));

        var draft = await Chat.AskAsync("The Contoso client.");

        // The assistant shows what it would send and waits: nothing is queued until the user says so.
        Assert.StartsWith(ScriptedInterviewer.Draft, draft.Text);
        Assert.Contains("VPN client will not connect", draft.Text);
        Assert.Contains("Shall I send this to the service desk?", draft.Text);
        Assert.Equal(["Send to the service desk", "Don't send"], draft.Buttons);
        Assert.Null(draft.Notice);
        Assert.Empty(App.Publisher.Sent);

        var confirmation = await Chat.SendDraftAsync();

        Assert.Equal(ScriptedInterviewer.Confirmation, confirmation.Text);
        Assert.Equal("Sent to the service desk", confirmation.Notice);
        Assert.Empty(confirmation.Buttons);
        Assert.Single(App.Publisher.Sent);
        Assert.Empty((await Chat.RepliesAsync())[1].Buttons); // the old draft can no longer be answered
    }

    [Fact]
    public async Task TurningTheDraftDown_SendsNothing_AndTheButtonsGoAway()
    {
        await Chat.AskAsync("My VPN is down.");
        await Chat.AskAsync("The Contoso client.");

        var reply = await Chat.DeclineDraftAsync();

        Assert.Equal(ScriptedInterviewer.Declined, reply.Text);
        Assert.Empty(reply.Buttons);
        Assert.Null(reply.Notice);
        Assert.Empty(App.Publisher.Sent);
    }

    [Fact]
    public async Task TypingAChangeInsteadOfPressingAButton_AlsoAnswersTheDraft()
    {
        await Chat.AskAsync("My VPN is down.");
        await Chat.AskAsync("The Contoso client.");

        var confirmation = await Chat.AskAsync("Yes please");

        Assert.Equal(ScriptedInterviewer.Confirmation, confirmation.Text);
        Assert.Single(App.Publisher.Sent);
    }

    [Fact]
    public async Task TheTicket_CarriesASummaryAndAConversationSummary_InSeparateFields()
    {
        await Chat.AskAsync("My VPN is down.");
        await Chat.AskAsync("The Contoso client.");
        await Chat.SendDraftAsync();

        var ticket = Assert.Single(App.Publisher.Sent);
        Assert.Equal("VPN client will not connect", ticket.Subject);
        Assert.Equal("The user cannot connect to the corporate VPN.", ticket.Summary);
        Assert.Contains("Contoso client", ticket.ConversationSummary);
        Assert.NotEqual(ticket.Summary, ticket.ConversationSummary);
        Assert.Equal(
            ["Summary", "Conversation so far", "Recommended action", "Steps already tried"],
            ticket.Details!.Answers.Select(a => a.Label));
        Assert.Null(ticket.Attachments);
    }

    [Fact]
    public async Task TheInterviewer_SeesTheWholeConversation_OnTheSecondTurn()
    {
        await Chat.AskAsync("My VPN is down.");
        await Chat.AskAsync("The Contoso client.");

        var seen = App.Interviewer.Conversations.Last();
        Assert.Contains("user: My VPN is down.", seen);
        Assert.Contains($"assistant: {ScriptedInterviewer.Question}", seen);
        Assert.EndsWith("user: The Contoso client.", seen.TrimEnd());
    }
}
