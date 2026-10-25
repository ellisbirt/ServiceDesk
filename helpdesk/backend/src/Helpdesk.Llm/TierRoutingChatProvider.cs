namespace Helpdesk.Llm;

/// <summary>
/// An <see cref="IChatProvider"/> that sends each request to the model for its <see cref="ChatRequest.Tier"/>, within
/// one vendor. <see cref="Provider"/>, <see cref="Model"/> and <see cref="Capabilities"/> describe the Standard tier.
/// A non-streamed Fast request the model refuses is repeated once on the Standard model.
/// </summary>
public sealed class TierRoutingChatProvider(ILlmProviderRouter router, string role) : IChatProvider
{
    private IChatProvider Standard => router.GetChat(role, ModelTier.Standard);

    public string Provider => Standard.Provider;
    public string Model => Standard.Model;
    public LlmCapabilities Capabilities => Standard.Capabilities;

    public async Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await router.GetChat(role, request.Tier).CompleteAsync(request, progress, cancellationToken);
        }
        catch (LlmRefusalException) when (request.Tier == ModelTier.Fast && router.GetChat(role, ModelTier.Fast).Model != Standard.Model)
        {
            return await Standard.CompleteAsync(request with { Tier = ModelTier.Standard }, progress, cancellationToken);
        }
    }

    // Not retried: text may already have reached the user, and a second stream would repeat it.
    public Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default) =>
        router.GetChat(role, request.Tier).StreamAsync(request, progress, cancellationToken);
}
