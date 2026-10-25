using Helpdesk.Core.Interfaces;
using Helpdesk.Llm;

namespace Helpdesk.Core;

/// <param name="Lane">The action lane being answered.</param>
/// <param name="Complexity">The classifier's rating of how hard the request is to resolve.</param>
/// <param name="EstimatedInputTokens">Rough size of everything sent to the model: message, history, KB articles and search results.</param>
/// <param name="UserTurns">User messages in the conversation, including the current one.</param>
public readonly record struct InferenceRouteContext(string Lane, RequestComplexity Complexity, int EstimatedInputTokens, int UserTurns);

public readonly record struct RouteDecision(ModelTier Tier, string Reason);

/// <summary>
/// Chooses which of the vendor's models answers a question, to keep cost down: small, routine questions go to the Fast
/// tier and only large or complex ones to the Deep tier. It is fixed in code rather than configured, so behaviour is the
/// same everywhere. It reads only server-side facts and the classifier's rating, never a request to use a model.
/// The classifier, the policy coach and the ticket interviewer do not use it: they have fixed tiers.
/// </summary>
public static class InferenceRoutePolicy
{
    /// <summary>A simple question at or under this size, early in a conversation, is answered on the Fast tier.</summary>
    public const int FastMaxTokens = 1500;
    public const int FastMaxUserTurns = 4;

    /// <summary>An input this large is long-context analysis (pasted logs, long conversations) and goes to the Deep tier.</summary>
    public const int DeepMinTokens = 12_000;

    public const string ReasonLargeInput = "large-input";
    public const string ReasonComplex = "complex";
    public const string ReasonSimpleSmall = "simple-small";
    public const string ReasonDefault = "default";

    /// <summary>About four characters per token, which is close enough to separate small from large.</summary>
    public static int EstimateTokens(IEnumerable<string?> texts) =>
        (int)Math.Min(int.MaxValue, (texts.Sum(t => (long)(t?.Length ?? 0)) + 3) / 4);

    public static RouteDecision Choose(InferenceRouteContext context)
    {
        if (context.EstimatedInputTokens >= DeepMinTokens) return new(ModelTier.Deep, ReasonLargeInput);
        if (context.Complexity == RequestComplexity.Complex) return new(ModelTier.Deep, ReasonComplex);
        if (context.Complexity == RequestComplexity.Simple
            && context.EstimatedInputTokens <= FastMaxTokens
            && context.UserTurns <= FastMaxUserTurns)
        {
            return new(ModelTier.Fast, ReasonSimpleSmall);
        }
        return new(ModelTier.Standard, ReasonDefault);
    }
}
