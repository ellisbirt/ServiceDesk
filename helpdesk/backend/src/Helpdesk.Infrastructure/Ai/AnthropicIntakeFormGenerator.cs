using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Ai;

public sealed class AnthropicIntakeFormGenerator(
    IAnthropicClient client,
    IOptions<AnthropicOptions> options) : IIntakeFormGenerator
{
    private readonly AnthropicOptions _options = options.Value;

    private static readonly JsonObject OutputConfig = new()
    {
        ["format"] = new JsonObject
        {
            ["type"] = "json_schema",
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string" },
                    ["submitLabel"] = new JsonObject { ["type"] = "string" },
                    ["fields"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["id"] = new JsonObject { ["type"] = "string" },
                                ["label"] = new JsonObject { ["type"] = "string" },
                                ["description"] = new JsonObject { ["type"] = "string" },
                                ["inputType"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["enum"] = new JsonArray("text", "textarea")
                                },
                                ["required"] = new JsonObject { ["type"] = "boolean" }
                            },
                            ["required"] = new JsonArray("id", "label", "description", "inputType", "required"),
                            ["additionalProperties"] = false
                        }
                    }
                },
                ["required"] = new JsonArray("title", "submitLabel", "fields"),
                ["additionalProperties"] = false
            }
        }
    };

    public async Task<IntakeForm> GenerateAsync(
        string userInput, string lane, CancellationToken cancellationToken = default, string? userAgent = null)
    {
        try
        {
            var raw = await client.CompleteAsync(
                Prompts.IntakeForm,
                PromptInput.WithUserAgent(
                    $"{PromptInput.Wrap("action_lane", lane)}\n\n{PromptInput.Wrap("user_query", userInput)}",
                    userAgent),
                _options.CoachTemperature,
                _options.InferenceMaxTokens,
                cancellationToken: cancellationToken,
                outputConfig: OutputConfig);
            return ParseForm(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IntakeFormGenerationException)
        {
            throw new IntakeFormGenerationException("The intake form could not be generated.", ex);
        }
    }

    internal static IntakeForm ParseForm(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3)
                throw new JsonException("Form output must contain exactly three properties.");

            var title = ReadString(root, "title", 80);
            var submitLabel = ReadString(root, "submitLabel", 40);
            var fieldsElement = root.GetProperty("fields");
            if (fieldsElement.ValueKind != JsonValueKind.Array || fieldsElement.GetArrayLength() is < 2 or > 6)
                throw new JsonException("Form must have between two and six fields.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var fields = new List<IntakeFormField>();
            foreach (var item in fieldsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 5)
                    throw new JsonException("Form field has an invalid shape.");

                var id = ReadString(item, "id", 32);
                if (!Regex.IsMatch(id, "^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant)
                    || !ids.Add(id))
                    throw new JsonException("Form field ids must be unique lowercase identifiers.");

                var inputType = ReadString(item, "inputType", 8);
                if (inputType is not ("text" or "textarea"))
                    throw new JsonException("Form field input type is not supported.");

                var requiredElement = item.GetProperty("required");
                if (requiredElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException("Form field required flag must be a boolean.");

                fields.Add(new IntakeFormField
                {
                    Id = id,
                    Label = ReadString(item, "label", 60),
                    Description = ReadString(item, "description", 160),
                    InputType = inputType,
                    Required = requiredElement.GetBoolean()
                });
            }

            return new IntakeForm { Title = title, SubmitLabel = submitLabel, Fields = fields };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IntakeFormGenerationException("The generated intake form did not match the required format.", ex);
        }
    }

    private static string ReadString(JsonElement parent, string name, int maxLength)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maxLength)
            throw new JsonException($"Form property '{name}' is invalid.");
        return value.GetString()!;
    }
}
