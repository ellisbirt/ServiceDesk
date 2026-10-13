using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using AgentDesk.Infrastructure.Intake;
using Helpdesk.Shared.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentDesk.Tests;

public sealed class AgentDeskFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Kb:Embedding:SweepEnabled", "false");
        // appsettings.Development.json points assist at Anthropic; tests must stay offline and free.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Roles:assist:Provider"] = "offline",
            ["Llm:Roles:assist:Model"] = "offline-assist",
        }));
    }
}

public class ApiTests(AgentDeskFactory factory) : IClassFixture<AgentDeskFactory>
{
    private const string Api = "/api/v1";

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<JsonNode> CreateTicketAsync(HttpClient client, string subject = "VPN drops every 10 minutes")
    {
        var res = await client.PostAsync($"{Api}/tickets", Json(new
        {
            subject,
            description = "Since this morning the VPN client disconnects every 10 minutes.",
            type = "incident",
            impact = 2,
            urgency = 2,
            category = "network"
        }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    [Fact]
    public async Task MetaConfig_IsAnonymousAndReportsDevMode()
    {
        var client = factory.CreateClient();
        var config = await client.GetFromJsonAsync<JsonNode>($"{Api}/meta/config");
        Assert.Equal("dev", (string?)config!["auth"]!["mode"]);
    }

    [Fact]
    public async Task Vocabulary_ExposesTransitions()
    {
        var vocab = await factory.CreateClient().GetFromJsonAsync<JsonNode>($"{Api}/meta/vocabulary");
        Assert.NotNull(vocab!["transitions"]);
    }

    [Fact]
    public async Task CreateAndListTickets_ComputesPriorityAndNumber()
    {
        var client = factory.CreateClient();
        var ticket = await CreateTicketAsync(client, "Printer on floor 3 jams");
        Assert.Equal("P3", (string?)ticket["priority"]);
        Assert.StartsWith("INC", (string?)ticket["number"]);
        Assert.Equal("new", (string?)ticket["status"]);

        var page = await client.GetFromJsonAsync<JsonNode>($"{Api}/tickets?q=printer");
        Assert.Contains(page!["items"]!.AsArray(), t => (string?)t!["id"] == (string?)ticket["id"]);
    }

    [Fact]
    public async Task Patch_WithStaleETag_Returns412()
    {
        var client = factory.CreateClient();
        var ticket = await CreateTicketAsync(client);
        var id = (string?)ticket["id"];

        var get = await client.GetAsync($"{Api}/tickets/{id}");
        Assert.NotNull(get.Headers.ETag);

        var stale = new HttpRequestMessage(HttpMethod.Patch, $"{Api}/tickets/{id}") { Content = Json(new { subject = "Changed" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);

        var fresh = new HttpRequestMessage(HttpMethod.Patch, $"{Api}/tickets/{id}") { Content = Json(new { subject = "Changed" }) };
        fresh.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(fresh)).StatusCode);
    }

    [Fact]
    public async Task Transition_RejectsInvalidMove()
    {
        var client = factory.CreateClient();
        var id = (string?)(await CreateTicketAsync(client))["id"];
        var res = await client.PostAsync($"{Api}/tickets/{id}/transition", Json(new { status = "closed" }));
        Assert.True(res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest, $"Got {res.StatusCode}");
    }

    [Fact]
    public async Task ReplyWithKb_CitingDraftPublishesItAndEmbeds()
    {
        var client = factory.CreateClient();
        var id = (string?)(await CreateTicketAsync(client))["id"];

        var draft = await (await client.PostAsync($"{Api}/tickets/{id}/assist/reply-with-kb", Json(new { })))
            .Content.ReadFromJsonAsync<JsonNode>();
        var replyBody = (string?)draft!["replyBody"];
        Assert.Contains("{{KB_ARTICLE}}", replyBody);

        var kbRes = await client.PostAsync($"{Api}/kb/articles",
            new StringContent(draft["article"]!.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, kbRes.StatusCode);
        var kb = (await kbRes.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("draft", (string?)kb["status"]);
        Assert.Equal("ready", (string?)kb["embedding"]!["status"]);
        Assert.True((bool)kb["embedding"]!["hasVector"]!);

        var body = replyBody!.Replace("{{KB_ARTICLE}}", $"{kb["number"]} — {kb["title"]}");
        var commentRes = await client.PostAsync($"{Api}/tickets/{id}/comments",
            Json(new { body, visibility = "public", citedKbArticleIds = new[] { (string?)kb["id"] } }));
        Assert.Equal(HttpStatusCode.Created, commentRes.StatusCode);
        var comment = (await commentRes.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("published", (string?)comment["publishedArticles"]![0]!["status"]);

        var after = await client.GetFromJsonAsync<JsonNode>($"{Api}/kb/articles/{kb["id"]}");
        Assert.Equal("published", (string?)after!["status"]);
    }

    [Fact]
    public async Task Assist_SuggestSolutions_FallsBackToOfflineProvider()
    {
        var client = factory.CreateClient();
        var id = (string?)(await CreateTicketAsync(client))["id"];
        var res = await client.PostAsync($"{Api}/tickets/{id}/assist/suggest-solutions", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.NotEmpty(body["suggestions"]!.AsArray());
        Assert.Equal("offline", (string?)body["aiAssisted"]!["provider"]);
    }

    [Fact]
    public async Task KbWrite_RequiresKbAuthorRole()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "AgentDesk.Agent");
        var res = await client.PostAsync($"{Api}/kb/articles", Json(new { title = "T", summary = "S", body = "B" }));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Api}/kb/articles")).StatusCode);
    }

    [Fact]
    public async Task AttachmentFlow_UploadCompleteDownload()
    {
        var client = factory.CreateClient();
        var id = (string?)(await CreateTicketAsync(client))["id"];
        var content = "hello from a log file"u8.ToArray();

        var upload = (await (await client.PostAsync($"{Api}/tickets/{id}/attachments",
            Json(new { fileName = "vpn log (copy) é.txt", contentType = "text/plain", size = content.Length })))
            .Content.ReadFromJsonAsync<JsonNode>())!;
        var attachmentId = (string?)upload["attachment"]!["id"];
        var uploadUrl = new Uri((string)upload["uploadUrl"]!);

        var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl.PathAndQuery) { Content = new ByteArrayContent(content) };
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(put)).StatusCode);

        var se = System.Web.HttpUtility.ParseQueryString(uploadUrl.Query)["se"]!;
        var extended = new HttpRequestMessage(HttpMethod.Put, uploadUrl.PathAndQuery.Replace($"se={se}", $"se={long.Parse(se) + 3600}"))
            { Content = new ByteArrayContent(content) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(extended)).StatusCode);

        var completed = await (await client.PostAsync($"{Api}/tickets/{id}/attachments/{attachmentId}/complete", null))
            .Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("available", (string?)completed!["status"]);

        var download = await client.GetFromJsonAsync<JsonNode>($"{Api}/tickets/{id}/attachments/{attachmentId}/download");
        var downloadUrl = new Uri((string)download!["downloadUrl"]!);
        var file = await client.GetAsync(downloadUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(content, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());

        // A read-signed URL must not authorise a write.
        var overwrite = new HttpRequestMessage(HttpMethod.Put, downloadUrl.PathAndQuery) { Content = new ByteArrayContent("evil"u8.ToArray()) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(overwrite)).StatusCode);
    }

    [Fact]
    public async Task Intake_IsIdempotentPerEventId()
    {
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IntakeEventProcessor>();
        var evt = new ProcessPrivateCorpTicketEvent
        {
            TenantId = "default",
            UserId = "user-42",
            ConversationId = Guid.NewGuid(),
            CorrelationId = "corr-1",
            SanitizedPayload = "My laptop will not connect to the office Wi-Fi since this morning."
        };
        var json = JsonSerializer.Serialize(evt);

        Assert.Equal(IntakeOutcome.Created, await processor.ProcessAsync(json, default));
        Assert.Equal(IntakeOutcome.Duplicate, await processor.ProcessAsync(json, default));
        Assert.Equal(IntakeOutcome.Invalid, await processor.ProcessAsync("{not json", default));
        Assert.Equal(IntakeOutcome.Invalid, await processor.ProcessAsync(JsonSerializer.Serialize(evt with { SanitizedPayload = " " }), default));
    }

    [Fact]
    public async Task Embedding_SkipsUnchangedContentAndRefreshesOnEdit()
    {
        using var scope = factory.Services.CreateScope();
        var embedder = scope.ServiceProvider.GetRequiredService<IKbEmbeddingService>();
        var article = new KbArticle { Id = "kb-test", TenantId = "default", Title = "Reset VPN profile", Body = "Remove and re-add the profile." };

        await embedder.ApplyAsync(article);
        Assert.Equal(EmbeddingStatuses.Ready, article.EmbeddingStatus);
        Assert.Equal(1536, article.Embedding!.Length);
        var firstAt = article.EmbeddedAt;
        var firstHash = article.EmbeddingContentHash;

        await embedder.ApplyAsync(article);
        Assert.Equal(firstAt, article.EmbeddedAt);

        article.Body = "Delete the VPN profile, reboot, then import it again.";
        await embedder.ApplyAsync(article);
        Assert.NotEqual(firstHash, article.EmbeddingContentHash);
    }
}

public class AuthModeTests
{
    private sealed class ModeFactory(string environment, params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        }
    }

    [Fact]
    public async Task JwtMode_RejectsRequestsWithoutToken()
    {
        await using var factory = new ModeFactory("Production",
            ("Auth:Enforce", "true"), ("Auth:Authority", "https://login.example.com/tenant/v2.0"), ("Auth:Audience", "api://agentdesk"));
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/tickets")).StatusCode);
        var config = await client.GetFromJsonAsync<JsonNode>("/api/v1/meta/config");
        Assert.Equal("jwt", (string?)config!["auth"]!["mode"]);
        Assert.True((bool)config["auth"]!["hubTicketRequired"]!);
    }

    [Fact]
    public async Task AnonymousMode_AllowsRequestsAsDefaultAgent()
    {
        await using var factory = new ModeFactory("Production");
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/tickets")).StatusCode);
        var config = await client.GetFromJsonAsync<JsonNode>("/api/v1/meta/config");
        Assert.Equal("anonymous", (string?)config!["auth"]!["mode"]);
    }
}
