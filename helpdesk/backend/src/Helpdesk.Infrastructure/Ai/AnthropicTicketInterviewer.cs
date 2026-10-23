using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Ai;

/// <summary>
/// Lets the model run the hand-off conversation: each turn it either asks for what is missing or raises the ticket.
/// There is no fixed form; what to ask depends on what the user has already said.
/// </summary>
public sealed class AnthropicTicketInterviewer(IChatProvider client, IOptions<AnthropicOptions> options) : ITicketInterviewer
{
    private readonly AnthropicOptions _options = options.Value;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["raiseTicket"] = new JsonObject { ["type"] = "boolean" },
            ["message"] = new JsonObject { ["type"] = "string" },
            ["title"] = new JsonObject { ["type"] = "string" },
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["conversationSummary"] = new JsonObject { ["type"] = "string" },
            ["recommendedAction"] = new JsonObject { ["type"] = "string" },
            ["stepsTried"] = new JsonObject { ["type"] = "string" }
        },
        ["required"] = new JsonArray("raiseTicket", "message", "title", "summary", "conversationSummary", "recommendedAction", "stepsTried"),
        ["additionalProperties"] = false
    };

    public async Task<TicketInterview> InterviewAsync(
        string conversation, CancellationToken cancellationToken = default, string? userAgent = null, bool mustRaise = false)
    {
        try
        {
            var raw = await client.CompleteAsync(
                new ChatRequest(
                    mustRaise ? Prompts.TicketInterviewer + "\n" + Prompts.TicketInterviewerFinalTurn : Prompts.TicketInterviewer,
                    PromptInput.WithUserAgent(PromptInput.Wrap("conversation", conversation), userAgent),
                    _options.CoachTemperature, _options.InferenceMaxTokens, ResponseSchema: Schema),
                cancellationToken: cancellationToken);
            return Parse(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TicketInterviewException and not LlmRefusalException)
        {
            throw new TicketInterviewException("The ticket interview could not be completed.", ex);
        }
    }

    internal static TicketInterview Parse(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 7)
                throw new JsonException("Interview output must contain exactly seven properties.");

            var raise = root.GetProperty("raiseTicket");
            if (raise.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("raiseTicket must be a boolean.");

            var message = Text(root, "message", 2000);
            if (message.Length == 0) throw new JsonException("message is required.");

            if (!raise.GetBoolean()) return new TicketInterview(false, message, null, null, null, null, null);

            var title = Text(root, "title", 120);
            var summary = Text(root, "summary", 4000);
            if (title.Length == 0 || summary.Length == 0) throw new JsonException("A ticket needs a title and summary.");

            return new TicketInterview(true, message, title, summary, NullIfBlank(Text(root, "conversationSummary", 4000)),
                NullIfBlank(Text(root, "recommendedAction", 2000)), NullIfBlank(Text(root, "stepsTried", 2000)));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TicketInterviewException("The interview output did not match the required format.", ex);
        }
    }

    private static string Text(JsonElement parent, string name, int maxLength)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maxLength)
            throw new JsonException($"Property '{name}' is invalid.");
        return value.GetString()!.Trim();
    }

    private static string? NullIfBlank(string text) => text.Length == 0 ? null : text;
}
