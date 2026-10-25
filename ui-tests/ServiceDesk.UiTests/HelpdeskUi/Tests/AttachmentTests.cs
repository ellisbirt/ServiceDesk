using System.Text;
using ServiceDesk.UiTests.Infrastructure;

namespace ServiceDesk.UiTests.HelpdeskUi;

public sealed class AttachmentTests(UiFixture fixture) : UiTest(fixture)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private static string LongLog(int lines = 40) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"2026-10-24 10:00:{i % 60:D2} ERROR disk full on /var (attempt {i})"));

    [Fact]
    public async Task TheAttachButton_IsThere()
    {
        Assert.True(await Chat.HasAttachButtonAsync());
    }

    [Fact]
    public async Task AttachingALogFile_ShowsItAsAChip_AndTheModelReadsIt()
    {
        var path = TempFile("app.log", "starting\nERROR disk full on /var\nshutting down\n");

        await Chat.AttachAsync(path);
        var kind = await Chat.Attachments.WaitForUploadedAsync("app.log");
        Assert.Equal("Text", kind);

        var reply = await Chat.AskAsync("Why does it fail? See the log.");

        var user = (await Chat.MessagesAsync()).First(m => m.IsUser);
        Assert.Equal("Why does it fail? See the log.", user.Text);
        Assert.Equal(["app.log"], user.Files);
        Assert.Contains("It reports that the disk is full", reply.Text);
        Assert.True(await Chat.Attachments.IsEmptyAsync()); // the tray empties once the files are sent
        var call = Assert.Single(App.Inference.Calls);
        Assert.Equal(["app.log"], call.Files);
        Assert.Contains("[BEGIN UNTRUSTED attachment", call.FileText); // file text reaches the model as untrusted data
    }

    [Fact]
    public async Task SendingOnlyAFile_UsesADefaultMessage()
    {
        await Chat.AttachAsync(TempFile("notes.txt", "some notes"));
        await Chat.Attachments.WaitForUploadedAsync("notes.txt");

        await Chat.ClickSendAsync();
        await Chat.WaitForReplyAsync(1);

        Assert.Equal("See the attached file.", (await Chat.MessagesAsync())[0].Text);
    }

    [Fact]
    public async Task AFileTakenOutBeforeSending_IsNotSent()
    {
        await Chat.AttachAsync(TempFile("wrong.txt", "the wrong file"));
        await Chat.Attachments.WaitForUploadedAsync("wrong.txt");

        await Chat.Attachments.RemoveAsync("wrong.txt");
        await Chat.AskAsync("Never mind, just a question.");

        Assert.Empty(App.Inference.Calls.Single().Files);
        Assert.Empty((await Chat.MessagesAsync()).First(m => m.IsUser).Files);
    }

    [Fact]
    public async Task SeveralFiles_AreSentTogether()
    {
        await Chat.AttachAsync(TempFile("a.log", "one"), TempFile("b.csv", "x,y\n1,2\n"), TempFile("c.png", Png));
        await Chat.Attachments.WaitForUploadedAsync("a.log");
        await Chat.Attachments.WaitForUploadedAsync("b.csv");
        Assert.Equal("Image", await Chat.Attachments.WaitForUploadedAsync("c.png"));

        await Chat.AskAsync("Here are the files.");

        Assert.Equal(["a.log", "b.csv", "c.png"], App.Inference.Calls.Single().Files.Order());
        Assert.Equal(3, (await Chat.MessagesAsync()).First(m => m.IsUser).Files.Count);
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("setup.exe")]
    [InlineData("backup.zip")]
    [InlineData("old.doc")]
    public async Task UnsupportedFiles_AreTurnedAwayWithAReason(string name)
    {
        await Chat.AttachAsync(TempFile(name, "not allowed"));

        await Task.Delay(300);
        var rejections = await Chat.RejectionsAsync();
        Assert.Contains(rejections, r => r.StartsWith(name));
        Assert.True(await Chat.Attachments.IsEmptyAsync());
    }

    [Fact]
    public async Task AFileTheServerRefuses_ShowsTheReasonOnItsChip()
    {
        // The name says text, the content is a program: only the server, which reads the content, can tell.
        await Chat.AttachAsync(TempFile("notes.txt", [.. "MZ"u8, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0]));

        await Chat.Attachments.WaitUntilEmptyAsync().ContinueWith(_ => { });
        var rejections = await Chat.RejectionsAsync();
        var chips = await Chat.Attachments.NamesAsync();
        Assert.True(rejections.Any(r => r.Contains("notes.txt")) || chips.Contains("notes.txt"),
            "the refused file should be explained, either as a chip with an error or in the rejection list");
        Assert.Empty(App.Inference.Calls);
    }

    [Fact]
    public async Task PastingALongLog_BecomesAnAttachment_NotAWallOfText()
    {
        Assert.True(await Chat.PasteAsync(LongLog())); // the app took the paste over

        var name = await Chat.Attachments.WaitForUploadedMatchingAsync(new Regex(@"^pasted-log-\d{8}-\d{6}\.txt$"));
        Assert.Equal("", await Chat.MessageBoxAsync()); // the message box stays for the question
        Assert.Equal("Pasted text was added as an attachment.", await Chat.ComposerNoticeAsync());

        await Chat.AskAsync("What does this log say?");

        var call = Assert.Single(App.Inference.Calls);
        Assert.Equal([name], call.Files);
        Assert.Contains("ERROR disk full on /var (attempt 40)", call.FileText);
    }

    [Fact]
    public async Task APasteThatLooksLikeAStackTrace_BecomesAnAttachment_EvenWhenShort()
    {
        var trace = """
            System.NullReferenceException: Object reference not set to an instance of an object.
               at Orders.Repo.Save(Order order) in C:\src\Orders.cs:line 42
               at Orders.Service.Place(Order order) in C:\src\Service.cs:line 17
               at Web.Controllers.OrderController.Post() in C:\src\Controller.cs:line 9
               at Microsoft.AspNetCore.Mvc.Infrastructure.ActionMethodExecutor.Execute()
               at System.Threading.Tasks.Task.Run()
            """;

        await Chat.PasteAsync(trace);

        await Chat.Attachments.WaitForUploadedMatchingAsync(new Regex(@"^pasted-log-.*\.txt$"));
        Assert.Equal("", await Chat.MessageBoxAsync());
    }

    [Fact]
    public async Task APasteOfOrdinaryText_IsLeftToTheBrowser_AndMakesNoAttachment()
    {
        var takenOver = await Chat.PasteAsync("My Wi-Fi keeps dropping since this morning.");

        Assert.False(takenOver); // the app does not intercept it, so the text goes into the box as normal
        Assert.True(await Chat.Attachments.IsEmptyAsync());
        Assert.Null(await Chat.ComposerNoticeAsync());
    }

    [Fact]
    public async Task PasteAsTextInstead_PutsTheTextBack_AndDropsTheFile()
    {
        var log = LongLog(30);
        await Chat.PasteAsync(log);
        await Chat.Attachments.WaitForUploadedMatchingAsync(new Regex(@"^pasted-log-.*\.txt$"));

        await Chat.PasteAsTextInsteadAsync();

        await Chat.Attachments.WaitUntilEmptyAsync();
        Assert.Equal(log, (await Chat.MessageBoxAsync()).Replace("\r\n", "\n"));
        Assert.Null(await Chat.ComposerNoticeAsync());
    }

    [Fact]
    public async Task AFileAttachedEarlier_StillShowsOnItsMessage_WhenTheConversationContinues()
    {
        await Chat.AttachAsync(TempFile("app.log", "ERROR disk full on /var"));
        await Chat.Attachments.WaitForUploadedAsync("app.log");
        await Chat.AskAsync("See the log.");

        await Chat.AskAsync("And what should I do about it?");

        var users = (await Chat.MessagesAsync()).Where(m => m.IsUser).ToList();
        Assert.Equal(["app.log"], users[0].Files);
        Assert.Empty(users[1].Files);
        // the second question's history still points at the file, so the model gets an excerpt of it
        Assert.Contains("app.log", App.Inference.Calls.Last().Files);
    }

    [Fact]
    public async Task AFileAndAHandOff_RaiseATicketThatCarriesTheFile()
    {
        await Chat.AttachAsync(TempFile("vpn.log", "handshake timeout to 10.0.0.5"));
        await Chat.Attachments.WaitForUploadedAsync("vpn.log");

        await Chat.AskAsync("My VPN is down, log attached.");
        var draft = await Chat.AskAsync("The Contoso client.");
        Assert.Contains("Attached: vpn.log", draft.Text);
        await Chat.SendDraftAsync();

        var ticket = Assert.Single(App.Publisher.Sent);
        var file = Assert.Single(ticket.Attachments!);
        Assert.Equal("vpn.log", file.FileName);
        Assert.StartsWith("intake/", file.BlobPath);
        Assert.Equal("not_scanned", file.ScanStatus); // there is no scanner yet, and the ticket says so
        Assert.Contains("vpn.log", ticket.Details!.Answers.Single(a => a.Label == "Attachments").Value);
        Assert.Contains("[attached: vpn.log]", App.Interviewer.Conversations.First());
    }
}
