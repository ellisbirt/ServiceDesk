using System.Text.Json.Nodes;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Llm.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Api.Tests;

/// <summary>
/// Builds the shared <see cref="AnthropicChatProvider"/> from intake <see cref="AnthropicOptions"/>, exactly as
/// the DI wiring does, and keeps the positional call shape the intake tests were written against.
/// </summary>
internal sealed class TestAnthropic(HttpMessageHandler handler, AnthropicOptions? options = null)
{
    public AnthropicChatProvider Provider { get; } = new(
        new HttpClient(handler),
        DependencyInjection.FromAnthropic(Prepare(options ?? new AnthropicOptions { Model = "m" })),
        NullLogger<AnthropicChatProvider>.Instance);

    private static AnthropicOptions Prepare(AnthropicOptions o)
    {
        o.BaseUrl = "https://example.test/";
        return o;
    }

    public Task<string> CompleteAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens,
        JsonArray? tools = null, CancellationToken ct = default, JsonObject? outputConfig = null, IChatProgress? progress = null) =>
        Provider.CompleteAsync(Request(systemPrompt, userContent, temperature, maxTokens, tools, outputConfig), progress, ct);

    public Task<string> StreamAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens, IChatProgress progress,
        JsonArray? tools = null, CancellationToken cancellationToken = default) =>
        Provider.StreamAsync(Request(systemPrompt, userContent, temperature, maxTokens, tools, null), progress, cancellationToken);

    private static ChatRequest Request(string system, string user, double temperature, int maxTokens, JsonArray? tools, JsonObject? outputConfig) =>
        new(system, user, temperature, maxTokens, ResponseSchema: outputConfig?["format"]?["schema"]?.DeepClone().AsObject(), Tools: tools);
}

/// <summary>An in-memory <see cref="IChatProvider"/> that records each request and replies from a script.</summary>
internal class ScriptedChatProvider(Func<ChatRequest, string> reply, LlmCapabilities capabilities = LlmCapabilities.StructuredOutput)
    : IChatProvider
{
    public List<ChatRequest> Requests { get; } = [];
    public List<string> SystemPrompts => Requests.ConvertAll(r => r.SystemPrompt);
    public List<string> UserContents => Requests.ConvertAll(r => r.UserContent);

    /// <summary>The structured-output schemas in the Anthropic <c>output_config</c> shape, for older assertions.</summary>
    public List<JsonObject?> OutputConfigs => Requests.ConvertAll(r => r.ResponseSchema is null
        ? null
        : new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = r.ResponseSchema.DeepClone() } });

    public string Provider => "scripted";
    public string Model => "scripted-model";
    public LlmCapabilities Capabilities => capabilities;

    public Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(reply(request));
    }

    public Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
