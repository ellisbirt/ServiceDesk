using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace ServiceDesk.UiTests.AgentDeskUi;

/// <summary>
/// A stateful fake of the AgentDesk API, answered inside the browser by Playwright: just enough to walk the
/// ticket, KB-cited reply, knowledge-base flow. The SignalR hub is switched off, so the app falls back to plain requests.
/// </summary>
public sealed class AgentDeskApiMock
{
    private readonly string _now = DateTime.UtcNow.ToString("O");
    private JsonArray _timeline;
    private JsonArray _articles = [];

    public AgentDeskApiMock()
    {
        _timeline =
        [
            new JsonObject
            {
                ["id"] = "c-1", ["docType"] = "comment", ["ticketId"] = "t-1",
                ["author"] = new JsonObject { ["id"] = "dev-user", ["displayName"] = "Dev Agent" },
                ["createdAt"] = _now, ["visibility"] = "public", ["body"] = "We are looking into it."
            }
        ];
    }

    /// <summary>Every non-GET request the app made: its path (without <c>/api/v1</c>) and JSON body.</summary>
    public List<(string Path, JsonNode? Body)> Posts { get; } = [];

    public async Task RegisterAsync(IPage page)
    {
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.RouteAsync("**/api/v1/**", HandleAsync);
    }

    private static JsonArray Strings(params string[] values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    private static JsonArray Numbers(params int[] values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    private JsonObject Vocabulary() => new()
    {
        ["ticketStatuses"] = Strings("new", "open", "pending", "on_hold", "resolved", "closed", "cancelled"),
        ["activeStatuses"] = Strings("new", "open", "pending", "on_hold"),
        ["transitions"] = new JsonObject
        {
            ["new"] = Strings("open", "resolved"), ["open"] = Strings("pending", "resolved"), ["pending"] = Strings("open"),
            ["on_hold"] = Strings("open"), ["resolved"] = Strings("open", "closed"), ["closed"] = Strings(), ["cancelled"] = Strings()
        },
        ["pendingReasons"] = Strings("awaiting_requester"),
        ["ticketTypes"] = Strings("incident", "service_request"),
        ["channels"] = Strings("portal", "agent"),
        ["priorities"] = Strings("P1", "P2", "P3", "P4"),
        ["categories"] = Strings("network", "software"),
        ["resolutionCodes"] = Strings("fixed", "workaround"),
        ["visibilities"] = Strings("public", "internal"),
        ["kbStatuses"] = Strings("draft", "published", "archived"),
        ["impactLevels"] = Numbers(1, 2, 3),
        ["urgencyLevels"] = Numbers(1, 2, 3)
    };

    private JsonObject Ticket() => new()
    {
        ["id"] = "t-1", ["tenantId"] = "dev-tenant", ["createdAt"] = _now, ["updatedAt"] = _now, ["_etag"] = "\"etag-1\"",
        ["number"] = "INC-1001", ["subject"] = "VPN drops every few minutes",
        ["description"] = "Since this morning the VPN disconnects every 5 minutes.",
        ["type"] = "incident", ["status"] = "open", ["impact"] = 2, ["urgency"] = 2, ["priority"] = "P3", ["category"] = "network",
        ["requester"] = new JsonObject { ["id"] = "u-1", ["displayName"] = "Alex Requester", ["email"] = "alex@example.com" },
        ["channel"] = "portal",
        ["sla"] = new JsonObject
        {
            ["policyId"] = "default",
            ["firstResponseDueAt"] = DateTime.UtcNow.AddHours(4).ToString("O"),
            ["resolutionDueAt"] = DateTime.UtcNow.AddHours(24).ToString("O"),
            ["firstResponseBreached"] = false, ["resolutionBreached"] = false
        }
    };

    private static JsonObject NewArticle(JsonObject body, string now)
    {
        var article = (JsonObject)body.DeepClone();
        article["id"] = "kb-1";
        article["number"] = "KB-1001";
        article["status"] = "draft";
        article["visibility"] = body["visibility"]?.DeepClone() ?? "public";
        article["version"] = 1;
        article["createdAt"] = now;
        article["updatedAt"] = now;
        article["embedding"] = new JsonObject
        {
            ["status"] = "ready", ["provider"] = "fake", ["model"] = "fake-embed", ["dimensions"] = 64, ["embeddedAt"] = now
        };
        article["eTag"] = "\"kb-etag-1\"";
        return article;
    }

    private async Task HandleAsync(IRoute route)
    {
        var request = route.Request;
        var path = new Uri(request.Url).AbsolutePath["/api/v1".Length..];
        var method = request.Method;
        var body = string.IsNullOrEmpty(request.PostData) ? null : JsonNode.Parse(request.PostData);
        if (method != "GET") Posts.Add((path, body));

        Task Ok(JsonNode data) => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json", Body = data.ToJsonString()
        });

        switch ($"{method} {path}")
        {
            case "GET /meta/vocabulary": await Ok(Vocabulary()); break;
            case "GET /me":
                await Ok(new JsonObject
                {
                    ["tenantId"] = "dev-tenant", ["userId"] = "dev-user", ["displayName"] = "Dev Agent", ["roles"] = Strings("agent")
                });
                break;
            case "GET /assist":
                await Ok(new JsonObject
                {
                    ["available"] = true, ["provider"] = "fake", ["model"] = "fake-model", ["offline"] = true, ["features"] = Strings(),
                    ["improveModes"] = Strings("polish"), ["kbPlaceholder"] = "{{KB}}"
                });
                break;
            case "GET /tickets": await Ok(new JsonObject { ["items"] = new JsonArray(Ticket()) }); break;
            case "GET /tickets/t-1": await Ok(Ticket()); break;
            case "GET /tickets/t-1/timeline": await Ok(_timeline.DeepClone()); break;
            case "GET /tickets/t-1/attachments": await Ok(new JsonArray()); break;
            case "POST /tickets/t-1/assist/reply-with-kb":
                await Ok(new JsonObject
                {
                    ["article"] = new JsonObject
                    {
                        ["title"] = "Fix VPN disconnects",
                        ["summary"] = "Update the VPN client to stop drop-outs.",
                        ["resolution"] = "Install version 5.2 of the client.",
                        ["body"] = "## Fix\n\nInstall version 5.2 of the VPN client.",
                        ["visibility"] = "public"
                    },
                    ["replyBody"] = "Hi Alex, the steps in {{KB}} should fix this.",
                    ["placeholder"] = "{{KB}}",
                    ["aiAssisted"] = new JsonObject { ["provider"] = "fake", ["model"] = "fake-model", ["feature"] = "reply-with-kb" }
                });
                break;
            case "POST /kb/articles":
                _articles = [NewArticle((JsonObject)body!, _now)];
                await Ok(_articles[0]!.DeepClone());
                break;
            case "POST /tickets/t-1/comments":
            {
                var comment = new JsonObject
                {
                    ["id"] = $"c-{_timeline.Count + 1}", ["docType"] = "comment", ["ticketId"] = "t-1",
                    ["author"] = new JsonObject { ["id"] = "dev-user", ["displayName"] = "Dev Agent" },
                    ["createdAt"] = DateTime.UtcNow.ToString("O")
                };
                foreach (var (key, value) in (JsonObject)body!) comment[key] = value?.DeepClone();
                _timeline.Add(comment.DeepClone());
                foreach (var article in _articles.OfType<JsonObject>())
                {
                    article["status"] = "published";
                    article["eTag"] = "\"kb-etag-2\"";
                }
                await Ok(new JsonObject { ["ticket"] = Ticket(), ["comment"] = comment, ["publishedArticles"] = _articles.DeepClone() });
                break;
            }
            case "GET /kb/articles": await Ok(new JsonObject { ["items"] = _articles.DeepClone() }); break;
            case "GET /kb/articles/kb-1": await Ok(_articles[0]!.DeepClone()); break;
            case "GET /kb/articles/kb-1/versions": await Ok(new JsonArray()); break;
            default:
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 404, ContentType = "application/problem+json",
                    Body = new JsonObject { ["title"] = "Not found", ["detail"] = $"{method} {path}" }.ToJsonString()
                });
                break;
        }
    }
}
