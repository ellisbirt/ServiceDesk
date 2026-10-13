using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Helpdesk.Llm.Offline;

/// <summary>
/// Deterministic, network-free chat for dev and tests. With a response schema it returns minimal JSON that conforms
/// to it (first enum value, empty strings, zeros, empty arrays); otherwise a short canned reply.
/// </summary>
public sealed class OfflineChatProvider(LlmProviderOptions options) : IChatProvider
{
    public string Provider => LlmProviders.Offline;
    public string Model => string.IsNullOrEmpty(options.Model) ? "offline" : options.Model;
    public LlmCapabilities Capabilities => LlmCapabilities.Streaming | LlmCapabilities.StructuredOutput;

    public Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Respond(request));
    }

    public async Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default)
    {
        var text = Respond(request);
        foreach (var piece in Regex.Split(text, @"(?<=\s)"))
        {
            if (piece.Length > 0) await progress.OnTextAsync(piece, cancellationToken);
        }
        return text;
    }

    private static string Respond(ChatRequest request)
    {
        if (request.Tools is not null)
            throw new LlmCapabilityException("The offline provider does not support provider-native server tools.");
        if (request.ResponseSchema is not null)
            return SampleFor(request.ResponseSchema)?.ToJsonString() ?? "null";
        return "[offline model] No language model is configured, so this is a placeholder response. " +
               "Configure an Llm role to get real suggestions.";
    }

    /// <summary>Builds the smallest value that satisfies a JSON schema subset (type, enum, properties, items).</summary>
    public static JsonNode? SampleFor(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return JsonValue.Create("");
        if (obj["const"] is { } constant) return constant.DeepClone();
        if (obj["enum"] is JsonArray { Count: > 0 } values) return values[0]?.DeepClone();
        if (obj["anyOf"] is JsonArray { Count: > 0 } anyOf) return SampleFor(anyOf[0]);

        var type = obj["type"] switch
        {
            JsonArray types => types.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t != "null") ?? "null",
            JsonValue value => value.GetValue<string>(),
            _ => obj["properties"] is not null ? "object" : "string"
        };
        return type switch
        {
            "object" => new JsonObject((obj["properties"] as JsonObject ?? [])
                .Select(p => KeyValuePair.Create(p.Key, SampleFor(p.Value)))),
            "array" => obj["minItems"]?.GetValue<int>() is > 0
                ? new JsonArray(SampleFor(obj["items"]))
                : new JsonArray(),
            "integer" or "number" => JsonValue.Create(obj["minimum"]?.GetValue<double>() is { } min ? (int)Math.Ceiling(min) : 0),
            "boolean" => JsonValue.Create(false),
            "null" => null,
            _ => JsonValue.Create("")
        };
    }
}

/// <summary>
/// Deterministic, network-free embeddings: a hashed bag of lower-cased word tokens, L2-normalised. Texts that share
/// words get a positive cosine similarity, which is enough to exercise storage and similarity code offline.
/// </summary>
public sealed class OfflineEmbeddingProvider(LlmProviderOptions options) : IEmbeddingProvider
{
    public const int DefaultDimensions = 1536;

    public string Provider => LlmProviders.Offline;
    public string Model => string.IsNullOrEmpty(options.Model) ? "offline-hash" : options.Model;
    public int Dimensions { get; } = options.Dimensions is > 0 ? options.Dimensions.Value : DefaultDimensions;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<float[]> result = inputs.Select(Embed).ToList();
        LlmTelemetry.EmbeddedInputs.Add(inputs.Count, LlmTelemetry.Tags(Provider, Model));
        return Task.FromResult(result);
    }

    private float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (Match token in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+"))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Value));
            var index = (int)(BitConverter.ToUInt32(hash, 0) % (uint)Dimensions);
            vector[index] += (hash[4] & 1) == 0 ? 1f : -1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        }
        return vector;
    }
}
