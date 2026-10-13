using System.Net;
using System.Net.Http.Json;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

public class RedactionTests
{
    private static SensitiveDataRedactor Create(DlpOptions? options = null) => new(Options.Create(options ?? new DlpOptions()));

    private static readonly SensitiveDataRedactor Redactor = Create();

    private static DlpOptions Policy(string destination, string category, string action, string? tenant = null)
    {
        var map = new Dictionary<string, Dictionary<string, string>> { [destination] = new() { [category] = action } };
        return tenant is null
            ? new DlpOptions { Destinations = map }
            : new DlpOptions { Tenants = new() { [tenant] = map } };
    }

    [Theory]
    [InlineData("connect with password=hunter2 please", "hunter2")]
    [InlineData("{\"client_secret\": \"abc123def456\"}", "abc123def456")]
    [InlineData("Server=x;AccountKey=Zm9vYmFyYmF6cXV4;", "Zm9vYmFyYmF6cXV4")]
    [InlineData("curl -H 'Authorization: Bearer abcdefghijklmnop1234'", "abcdefghijklmnop1234")]
    [InlineData("key sk-ant-api03-abcdefghijklmnopqrstuv", "abcdefghijklmnopqrstuv")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("postgres://admin:s3cretPw@db.local/app", "s3cretPw")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK\n-----END RSA PRIVATE KEY-----", "MIIBOgIBAAJBAK")]
    [InlineData("card 4111 1111 1111 1111 expired", "4111")]
    public void Secrets_AreRemoved(string input, string leaked)
    {
        var result = Redactor.Redact(input);
        Assert.DoesNotContain(leaked, result.Text);
        Assert.True(result.Count >= 1);
    }

    [Theory]
    [InlineData("My password is hunter2; Outlook says it is incorrect.", "hunter2")]
    [InlineData("The token is abc123xyz, please help.", "abc123xyz")]
    [InlineData("My API key is \"abc 123 xyz\".", "abc 123 xyz")]
    public void NaturalLanguageSecrets_AreRemoved(string input, string leaked)
    {
        var result = Redactor.Redact(input);
        Assert.DoesNotContain(leaked, result.Text);
        Assert.True(result.Count >= 1);
    }

    [Theory]
    [InlineData("My VPN is down since the 3pm update on 10.0.0.5")]
    [InlineData("Order number 1234567890123 is not a card")] // fails the Luhn check
    [InlineData("How do I reset my password in Outlook?")]
    [InlineData("I can't log in because my password is incorrect even though I'm sure it's right.")]
    [InlineData("Windows build 10.0.19045.1234 fails with error 0x80070005")]
    [InlineData("server 192.168.100.200 port 8443")]
    [InlineData("")]
    public void OrdinaryText_IsUnchanged_ForEveryDestination(string input)
    {
        foreach (var destination in Enum.GetValues<EgressDestination>())
        {
            var result = Redactor.Redact(input, destination);
            Assert.Equal(input, result.Text);
            Assert.Equal(0, result.Count);
        }
    }

    [Fact]
    public void KeyName_IsKept_SoTheTextStillReads()
    {
        Assert.Equal("password=[REDACTED:secret] fails", Redactor.Redact("password=hunter2 fails").Text);
    }

    // ----- Categories beyond secrets -----

    [Theory]
    [InlineData("SSN is 123-45-6789 ok", "123-45-6789", "[REDACTED:id]")]
    [InlineData("NI number AB 12 34 56 C", "AB 12 34 56 C", "[REDACTED:id]")]
    [InlineData("pay to GB82 WEST 1234 5698 7654 32 today", "GB82 WEST 1234 5698 7654 32", "[REDACTED:iban]")]
    [InlineData("pay to DE89370400440532013000", "DE89370400440532013000", "[REDACTED:iban]")]
    public void GovernmentIds_AndIbans_AreRemovedForEveryDestination(string input, string leaked, string marker)
    {
        foreach (var destination in Enum.GetValues<EgressDestination>())
        {
            var result = Redactor.Redact(input, destination);
            Assert.DoesNotContain(leaked, result.Text);
            Assert.Contains(marker, result.Text);
        }
    }

    [Theory]
    [InlineData("SSN 000-12-3456")]                    // area 000 is never issued
    [InlineData("SSN 666-12-3456")]
    [InlineData("code GB82 WEST 1234 5698 7654 33")]   // fails the IBAN checksum
    public void LookalikesThatFailValidation_AreKept(string input)
    {
        Assert.Equal(input, Redactor.Redact(input).Text);
    }

    [Theory]
    [InlineData("mail bob@example.com about it", "bob@example.com")]
    [InlineData("call +44 20 7946 0958 now", "+44 20 7946 0958")]
    [InlineData("call (555) 123-4567 now", "(555) 123-4567")]
    [InlineData("call 555-123-4567 now", "555-123-4567")]
    public void EmailAndPhone_AreKeptForModelAndQueue_ButRemovedForSearch(string input, string value)
    {
        Assert.Contains(value, Redactor.Redact(input, EgressDestination.Model).Text);
        Assert.Contains(value, Redactor.Redact(input, EgressDestination.Queue).Text);

        var search = Redactor.Redact(input, EgressDestination.Search);
        Assert.DoesNotContain(value, search.Text);
        Assert.Equal(1, search.Count);

        var embedding = Redactor.Redact(input, EgressDestination.Embedding);
        Assert.DoesNotContain(value, embedding.Text);
        Assert.Equal(1, embedding.Count);
    }

    [Fact]
    public void Result_CountsByCategory()
    {
        var result = Redactor.Redact("a@b.com and c@d.org, password=x1, ssn 123-45-6789", EgressDestination.Search);

        Assert.Equal(4, result.Count);
        Assert.Equal(2, result.Categories!["Email"]);
        Assert.Equal(1, result.Categories["Secret"]);
        Assert.Equal(1, result.Categories["GovernmentId"]);
    }

    // ----- Policy -----

    [Fact]
    public void GlobalOverride_ChangesWhatTheModelSees()
    {
        var redactor = Create(Policy("Model", "Email", "Redact"));
        Assert.DoesNotContain("bob@example.com", redactor.Redact("mail bob@example.com", EgressDestination.Model).Text);
        Assert.Contains("bob@example.com", redactor.Redact("mail bob@example.com", EgressDestination.Queue).Text);
    }

    [Fact]
    public void TenantOverride_AppliesOnlyToThatTenant()
    {
        var redactor = Create(Policy("Queue", "Phone", "Redact", tenant: "strict"));

        Assert.DoesNotContain("555-123-4567", redactor.Redact("call 555-123-4567", EgressDestination.Queue, "strict").Text);
        Assert.Contains("555-123-4567", redactor.Redact("call 555-123-4567", EgressDestination.Queue, "relaxed").Text);
        Assert.Contains("555-123-4567", redactor.Redact("call 555-123-4567", EgressDestination.Queue).Text);
    }

    [Fact]
    public void TenantOverride_BeatsGlobal()
    {
        var options = Policy("Search", "Email", "Allow", tenant: "t1");
        options.Destinations["Search"] = new() { ["Email"] = "Redact" };
        var redactor = Create(options);

        Assert.Contains("a@b.com", redactor.Redact("a@b.com", EgressDestination.Search, "t1").Text);
        Assert.DoesNotContain("a@b.com", redactor.Redact("a@b.com", EgressDestination.Search, "t2").Text);
    }

    [Fact]
    public void EmbeddingDestination_CanBeRelaxedPerTenant()
    {
        var redactor = Create(Policy("Embedding", "Email", "Allow", tenant: "kb-open"));

        Assert.Contains("a@b.com", redactor.Redact("a@b.com", EgressDestination.Embedding, "kb-open").Text);
        Assert.DoesNotContain("a@b.com", redactor.Redact("a@b.com", EgressDestination.Embedding, "other").Text);
    }

    [Fact]
    public void Secrets_CannotBeAllowed_EvenIfConfigured()
    {
        var options = Policy("Model", "Secret", "Allow");
        var policy = new DlpPolicy(options);

        Assert.Contains(policy.Errors, e => e.Contains("cannot be Allow"));
        Assert.DoesNotContain("hunter2", Create(options).Redact("password=hunter2").Text);
    }

    [Fact]
    public void NamesAreCaseInsensitive_AndBadNamesAreReported()
    {
        Assert.Empty(new DlpPolicy(Policy("search", "EMAIL", "allow")).Errors);
        Assert.Equal(3, new DlpPolicy(new DlpOptions
        {
            Destinations = new()
            {
                ["Nowhere"] = new() { ["Email"] = "Redact" },
                ["Model"] = new() { ["Birthday"] = "Redact", ["Phone"] = "Maybe" }
            }
        }).Errors.Count);
    }

    // ----- Through the pipeline -----

    private static HttpRequestMessage Request(string text, object? history = null) =>
        new(HttpMethod.Post, "/api/v1/chat/message")
        {
            Content = JsonContent.Create(new { messageText = text, conversationId = Guid.NewGuid(), conversationHistory = history ?? Array.Empty<object>() })
        };

    [Fact]
    public async Task PrivateCorp_Ticket_NeverContainsTheSecret()
    {
        using var f = new ApiFactory();
        f.Router.Handler = _ => (ActionLanes.PrivateCorp, "");
        var res = await f.CreateClient().SendAsync(Request("VPN fails, password=hunter2"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(f.Publisher.Sent);
        var evt = new { SanitizedPayload = await PendingPayload(f, res) };
        Assert.Equal("VPN fails, password=[REDACTED:secret]", evt.SanitizedPayload);
    }

    [Fact]
    public async Task PrivateCorp_Reply_AsksTheUserToCompleteTheForm()
    {
        using var f = new ApiFactory();
        f.Router.Handler = _ => (ActionLanes.PrivateCorp, "");
        var body = await (await f.CreateClient().SendAsync(Request("VPN is down"))).Content.ReadFromJsonAsync<HelpdeskApiResponse>();

        Assert.Contains("Complete the form", body!.ResponseText);
    }

    private static async Task<string?> PendingPayload(ApiFactory f, HttpResponseMessage res) =>
        f.PendingEscalation("dev-tenant", "dev-user", (await res.Content.ReadFromJsonAsync<HelpdeskApiResponse>())!)?.Payload;

    [Fact]
    public async Task Classifier_ReceivesRedactedMessageAndHistory()
    {
        using var f = new ApiFactory();
        string? seen = null;
        f.Router.Handler = text => { seen = text; return (ActionLanes.PublicTech, ""); };
        var conversationId = Guid.NewGuid();
        var history = f.SignedHistory("dev-tenant", "dev-user", conversationId, ("user", "my password is letmein99; Outlook rejects it"));
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/message")
        {
            Content = JsonContent.Create(new
            {
                messageText = "why does token=abcd1234efgh fail? my password is hunter2; Outlook rejects it",
                conversationId,
                conversationHistory = history
            })
        };
        await f.CreateClient().SendAsync(req);

        Assert.DoesNotContain("abcd1234efgh", seen);
        Assert.DoesNotContain("hunter2", seen);
        Assert.DoesNotContain("letmein99", f.Router.LastHistory![0].Content);
    }

    [Fact]
    public async Task SearchLane_SendsTheSearchProviderNoEmailOrPhone_ButTheModelSeesThem()
    {
        using var f = new ApiFactory();
        string? classifierSaw = null;
        f.Router.Handler = text => { classifierSaw = text; return (ActionLanes.PublicSearch, ""); };
        await f.CreateClient().SendAsync(Request("is azure down? reach me at bob@example.com or +44 20 7946 0958"));

        Assert.Contains("bob@example.com", classifierSaw);
        Assert.DoesNotContain("bob@example.com", f.Search.LastQuery);
        Assert.DoesNotContain("7946", f.Search.LastQuery);
    }

    [Fact]
    public async Task Ticket_KeepsAContactEmail_UnlessTheQueuePolicyRedactsIt()
    {
        using var permissive = new ApiFactory();
        permissive.Router.Handler = _ => (ActionLanes.PrivateCorp, "");
        var permissiveRes = await permissive.CreateClient().SendAsync(Request("VPN down, reach me at bob@example.com"));
        Assert.Contains("bob@example.com", await PendingPayload(permissive, permissiveRes));

        using var strict = new ApiFactory(settings: new Dictionary<string, string> { ["Dlp:Destinations:Queue:Email"] = "Redact" });
        strict.Router.Handler = _ => (ActionLanes.PrivateCorp, "");
        var strictRes = await strict.CreateClient().SendAsync(Request("VPN down, reach me at bob@example.com"));
        Assert.DoesNotContain("bob@example.com", await PendingPayload(strict, strictRes));
    }

    [Fact]
    public async Task TenantPolicy_FromConfiguration_IsAppliedByTenantClaim()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Dlp:Tenants:tenant-strict:Model:Email"] = "Redact" });
        string? seen = null;
        f.Router.Handler = text => { seen = text; return (ActionLanes.PublicTech, ""); };

        HttpRequestMessage As(string tenant)
        {
            var req = Request("mail bob@example.com");
            req.Headers.Add("X-Dev-Tenant", tenant);
            return req;
        }

        await f.CreateClient().SendAsync(As("tenant-strict"));
        Assert.DoesNotContain("bob@example.com", seen);

        await f.CreateClient().SendAsync(As("tenant-other"));
        Assert.Contains("bob@example.com", seen);
    }
}
