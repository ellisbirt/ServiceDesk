using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using AgentDesk.Core.Services;
using AgentDesk.Infrastructure.Blobs;
using AgentDesk.Infrastructure.Intake;
using Helpdesk.Shared.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentDesk.Tests;

/// <summary>Files attached in the helpdesk chat arrive on the intake event and are recorded on the ticket without copying.</summary>
public class IntakeAttachmentTests(AgentDeskFactory factory) : IClassFixture<AgentDeskFactory>
{
    private const string Tenant = "default";
    private const string Api = "/api/v1";

    private static TicketAttachment File(Guid eventId, string name = "pasted-log.txt", string? path = null, string scan = "not_scanned",
        string? sha = null, string contentType = "text/plain", long size = 11) => new()
    {
        Id = Guid.NewGuid(),
        FileName = name,
        ContentType = contentType,
        Size = size,
        Sha256 = sha,
        BlobPath = path ?? $"intake/{Tenant}/{eventId:N}/{Guid.NewGuid():N}/{name}",
        ScanStatus = scan
    };

    private static ProcessPrivateCorpTicketEvent Event(params TicketAttachment[] files) => new()
    {
        TenantId = Tenant,
        UserId = "user-9",
        ConversationId = Guid.NewGuid(),
        CorrelationId = "corr",
        SanitizedPayload = "Printer will not print.",
        Attachments = files.Length == 0 ? null : files
    };

    private InMemoryBlobStore Blobs() => (InMemoryBlobStore)factory.Services.GetRequiredService<IBlobStore>();

    private static void Upload(InMemoryBlobStore blobs, TicketAttachment file, string content = "hello world")
        => blobs.Put(file.BlobPath, System.Text.Encoding.UTF8.GetBytes(content), file.ContentType);

    private async Task<(Ticket Ticket, IReadOnlyList<Attachment> Attachments)> ProcessAsync(ProcessPrivateCorpTicketEvent evt, bool again = false)
    {
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IntakeEventProcessor>();
        var json = JsonSerializer.Serialize(evt);
        Assert.Equal(IntakeOutcome.Created, await processor.ProcessAsync(json, default));
        if (again) Assert.Equal(IntakeOutcome.Duplicate, await processor.ProcessAsync(json, default));

        var tickets = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        var ticket = (await tickets.GetAsync(Tenant, TicketService.DeterministicId(Tenant, evt.EventId.ToString("D"))))!;
        var attachments = await scope.ServiceProvider.GetRequiredService<IAttachmentRepository>().ListForTicketAsync(Tenant, ticket.Id);
        return (ticket, attachments);
    }

    [Fact]
    public async Task EventAttachments_BecomeAttachmentsOnTheTicket_PointingAtTheSharedBlob()
    {
        var sha = new string('a', 64).ToUpperInvariant();
        var evt = Event();
        var file = File(evt.EventId, "pasted log (1).txt", sha: sha);
        var image = File(evt.EventId, "screen.png", contentType: "image/png", size: 5);
        Upload(Blobs(), file);
        Upload(Blobs(), image, "12345");
        evt = evt with { Attachments = [file, image] };

        var (ticket, attachments) = await ProcessAsync(evt);

        Assert.Equal(2, attachments.Count);
        Assert.Equal(2, ticket.AttachmentCount);
        var log = attachments.Single(a => a.FileName.StartsWith("pasted log"));
        Assert.Equal(file.BlobPath, log.BlobPath); // verbatim, not PathFor(...)
        Assert.Equal(AttachmentStatuses.Available, log.Status);
        Assert.Equal("text/plain", log.ContentType);
        Assert.Equal(11, log.Size);
        Assert.Equal(sha.ToLowerInvariant(), log.Sha256);
        Assert.Equal("user-9", log.UploadedBy.Id);
        Assert.Equal(ticket.Id, log.TicketId);
        Assert.Equal(5, attachments.Single(a => a.FileName == "screen.png").Size);
    }

    [Fact]
    public async Task TheScanStatus_IsCarriedThrough_AndNeverPresentedAsClean()
    {
        var evt = Event();
        var file = File(evt.EventId);
        Upload(Blobs(), file);

        var (_, attachments) = await ProcessAsync(evt with { Attachments = [file] });

        Assert.Equal("not_scanned", attachments.Single().ScanResult);
    }

    [Fact]
    public async Task ARedeliveredEvent_AddsNothing()
    {
        var evt = Event();
        var file = File(evt.EventId);
        Upload(Blobs(), file);

        var (ticket, attachments) = await ProcessAsync(evt with { Attachments = [file] }, again: true);

        Assert.Single(attachments);
        Assert.Equal(1, ticket.AttachmentCount);
        Assert.Equal(AttachmentService.IntakeAttachmentId(Tenant, ticket.Id, file.Id.ToString("D")), attachments.Single().Id);
    }

    [Fact]
    public async Task AnEventWithoutAttachments_ImportsAsBefore()
    {
        var (ticket, attachments) = await ProcessAsync(Event());

        Assert.Empty(attachments);
        Assert.Equal(0, ticket.AttachmentCount);
    }

    [Theory]
    [InlineData("intake/other-tenant/c/a/log.txt")]          // another tenant's folder
    [InlineData("intake/default/../other/a/log.txt")]         // traversal
    [InlineData("intake/default/c/../../x/log.txt")]
    [InlineData("/intake/default/c/a/log.txt")]               // absolute
    [InlineData("intake\\default\\c\\a\\log.txt")]            // backslashes
    [InlineData("intake/default/c/a/lo\u0001g.txt")]          // control character
    [InlineData("intake/default//a/log.txt")]                 // empty segment
    [InlineData("tickets/default/t/a")]                       // not under intake/
    [InlineData("intake/defaultx/c/a/log.txt")]               // tenant prefix is a whole segment
    public async Task UnsafeBlobPaths_AreSkipped_WithoutFailingTheTicket(string path)
    {
        var evt = Event();
        var bad = File(evt.EventId, path: path);
        var good = File(evt.EventId, "good.txt");
        Upload(Blobs(), good);
        if (bad.BlobPath.Length > 0) Blobs().Put(bad.BlobPath, [1], "text/plain");

        var (ticket, attachments) = await ProcessAsync(evt with { Attachments = [bad, good] });

        Assert.Equal("good.txt", attachments.Single().FileName);
        Assert.Equal(1, ticket.AttachmentCount);
    }

    [Fact]
    public async Task AnOverlongPath_IsSkipped()
    {
        var evt = Event();
        var bad = File(evt.EventId, path: $"intake/{Tenant}/c/a/" + new string('x', 1100));

        var (_, attachments) = await ProcessAsync(evt with { Attachments = [bad] });

        Assert.Empty(attachments);
    }

    [Fact]
    public async Task AFileMissingFromStorage_IsNotListed_ButTheTicketIsStillCreated()
    {
        var evt = Event();
        var missing = File(evt.EventId, "gone.txt"); // never uploaded
        var present = File(evt.EventId, "here.txt");
        Upload(Blobs(), present);

        var (ticket, attachments) = await ProcessAsync(evt with { Attachments = [missing, present] });

        Assert.Equal(1, ticket.AttachmentCount);
        Assert.Equal(AttachmentStatuses.Available, attachments.Single(a => a.FileName == "here.txt").Status);
        var gone = attachments.Single(a => a.FileName == "gone.txt");
        Assert.Equal(AttachmentStatuses.Rejected, gone.Status);
        Assert.Equal("missing_blob", gone.ScanResult);
    }

    [Fact]
    public async Task TheNumberImported_IsCappedAtTheTicketLimit()
    {
        var max = factory.Services.GetRequiredService<IOptions<AttachmentOptions>>().Value.MaxPerTicket;
        var evt = Event();
        var files = Enumerable.Range(0, max + 5).Select(i => File(evt.EventId, $"f{i}.txt")).ToArray();
        foreach (var f in files) Upload(Blobs(), f);

        var (ticket, attachments) = await ProcessAsync(evt with { Attachments = files });

        Assert.Equal(max, attachments.Count(a => a.Status == AttachmentStatuses.Available));
        Assert.Equal(max, ticket.AttachmentCount);
    }

    [Fact]
    public async Task FileNamesAreSanitised_AndBadDetailsGetSafeDefaults()
    {
        var evt = Event();
        var odd = File(evt.EventId, "..\\..\\evil<name>.txt", contentType: " ", sha: "not-a-hash");
        odd = odd with { BlobPath = $"intake/{Tenant}/{evt.EventId:N}/a/evil.txt" };
        Upload(Blobs(), odd);

        var (_, attachments) = await ProcessAsync(evt with { Attachments = [odd] });

        var saved = attachments.Single();
        Assert.DoesNotContain('<', saved.FileName);
        Assert.DoesNotContain('\\', saved.FileName);
        Assert.Equal("application/octet-stream", saved.ContentType);
        Assert.Null(saved.Sha256);
    }

    [Fact]
    public async Task AnImportedFile_CanBeDownloaded_FromItsIntakePath()
    {
        var evt = Event();
        var file = File(evt.EventId, "boot.log");
        var content = "kernel: boot ok"u8.ToArray();
        Blobs().Put(file.BlobPath, content, "text/plain");

        var (ticket, attachments) = await ProcessAsync(evt with { Attachments = [file with { Size = content.Length }] });

        var client = factory.CreateClient();
        var list = await client.GetFromJsonAsync<JsonArray>($"{Api}/tickets/{ticket.Id}/attachments");
        Assert.Contains(list!, n => (string?)n!["fileName"] == "boot.log");
        var download = await client.GetFromJsonAsync<JsonNode>($"{Api}/tickets/{ticket.Id}/attachments/{attachments.Single().Id}/download");
        var res = await client.GetAsync(new Uri((string)download!["downloadUrl"]!).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(content, await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void MarkdownIsAllowedForAgentUploads()
    {
        var options = factory.Services.GetRequiredService<IOptions<AttachmentOptions>>().Value;

        Assert.Contains(".md", options.AllowedExtensions);
        Assert.Contains("text/markdown", options.AllowedContentTypes);
    }
}
