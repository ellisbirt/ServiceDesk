using System.Collections.Concurrent;
using Helpdesk.Core.Interfaces;
using Helpdesk.Llm;
using Helpdesk.Shared.Contracts;
using Helpdesk.Shared.Events;

namespace Helpdesk.UiTests.Infrastructure;

/// <summary>
/// Stand-ins for everything that would call a model, a search provider or the ticket queue, so the browser tests are
/// deterministic, fast and offline. The real pipeline, hub, endpoints, attachment handling and front end run unchanged.
/// </summary>
public sealed class ScriptedRouter : IGuardrailRouterService
{
    public const string Guidance =
        "I can't share or reset someone else's password. The safe way is to ask for access to the files you need on your own account.";

    public ConcurrentQueue<string> Seen { get; } = new();

    public Task<Classification> ClassifyAndValidateAsync(
        string userInput, string tenantId, IReadOnlyList<ChatHistoryMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null)
    {
        Seen.Enqueue(userInput);
        var text = userInput.ToLowerInvariant();

        if (text.Contains("explode")) throw new GuardrailClassificationException("scripted failure");
        if (text.Contains("joke")) return Task.FromResult(new Classification(ActionLanes.NoMatch, ""));
        if ((text.Contains("colleague") || text.Contains("someone else")) && text.Contains("password"))
            return Task.FromResult(new Classification(ActionLanes.PolicyViolation, Guidance));
        if (text.Contains("vpn") || text.Contains("own work account") || text.Contains("raise a ticket"))
            return Task.FromResult(new Classification(ActionLanes.PrivateCorp, ""));
        // A reply to the interviewer's question carries on the hand-off, as the real classifier does from the history.
        // The same goes for the answer to a ticket draft, whether it agrees, declines or changes something.
        if (conversationHistory?.LastOrDefault(m => m.Role == "assistant")?.Content is { } last
            && (last.Contains(ScriptedInterviewer.Question) || last.TrimEnd().EndsWith(Helpdesk.Infrastructure.Ai.Prompts.TicketConfirmPrompt)))
            return Task.FromResult(new Classification(ActionLanes.PrivateCorp, ""));
        if (text.Contains("outage") || text.Contains("is azure down"))
            return Task.FromResult(new Classification(ActionLanes.PublicSearch, ""));
        return Task.FromResult(new Classification(ActionLanes.PublicTech, ""));
    }

    public Task<string> CoachAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null) =>
        Task.FromResult(Guidance);
}

public sealed record InferenceCall(
    string Question, int HistoryMessages, ModelTier Tier, IReadOnlyList<string> Files, string FileText);

public sealed class ScriptedInference : IPublicInferenceService
{
    public ConcurrentQueue<InferenceCall> Calls { get; } = new();

    /// <summary>How long between streamed fragments, so a test can see the reply arrive in pieces.</summary>
    public TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(60);

    public Task<string> ExecutePublicInferenceAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null,
        IReadOnlyList<ChatHistoryMessage>? conversationHistory = null, IReadOnlyList<KbHit>? knowledgeBase = null,
        ModelTier tier = ModelTier.Standard, AttachmentContext? attachments = null) =>
        ReplyAsync(userInput, conversationHistory?.Count ?? 0, tier, attachments, progress, cancellationToken);

    public Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null, IReadOnlyList<KbHit>? knowledgeBase = null,
        ModelTier tier = ModelTier.Standard, AttachmentContext? attachments = null) =>
        ReplyAsync(userInput, 0, tier, attachments, progress, cancellationToken, "Live status: all services are operating normally.");

    private async Task<string> ReplyAsync(
        string question, int history, ModelTier tier, AttachmentContext? attachments, IChatProgress? progress, CancellationToken ct,
        string? fixedReply = null)
    {
        var files = attachments?.Files.Select(f => f.FileName).ToList() ?? [];
        Calls.Enqueue(new InferenceCall(question, history, tier, files, attachments?.Text ?? ""));

        var text = fixedReply ?? Compose(question, attachments, files);
        if (progress is not null)
        {
            // A few fragments, so the live bubble is on screen before the finished one replaces it.
            foreach (var piece in text.Split(' ').Chunk(4).Select(c => string.Join(' ', c) + " "))
            {
                await progress.OnTextAsync(piece, ct);
                await Task.Delay(Pause, ct);
            }
        }
        return text;
    }

    private static string Compose(string question, AttachmentContext? attachments, List<string> files)
    {
        if (question.Contains("markdown", StringComparison.OrdinalIgnoreCase))
            return "**Bold step** then run `ipconfig /all`.\n\n- first item\n- second item\n\n<script>window.__injected = true</script>";

        if (files.Count > 0)
        {
            var finding = attachments!.Text.Contains("ERROR disk full") ? " It reports that the disk is full." : "";
            return $"I looked at {string.Join(", ", files)}.{finding}";
        }
        return "Here is how to fix that: restart the app, then check for updates and try again.";
    }
}

public sealed class ScriptedInterviewer : ITicketInterviewer
{
    public const string Question = "Which VPN client are you using?";
    public const string Confirmation = "Thanks, I've sent this to the service desk.";
    public const string Draft = "Here is what I'd send.";
    public const string Declined = "No problem, nothing has been sent.";

    public ConcurrentQueue<string> Conversations { get; } = new();

    public Task<TicketInterview> InterviewAsync(
        string conversation, CancellationToken cancellationToken = default, string? userAgent = null, bool mustRaise = false,
        bool proposalPending = false)
    {
        Conversations.Enqueue(conversation);
        var userTurns = conversation.Split('\n').Count(line => line.StartsWith("user:", StringComparison.Ordinal));
        if (userTurns < 2 && !mustRaise && !proposalPending)
            return Task.FromResult(new TicketInterview(false, Question, null, null, null, null, null));

        // The user's answer to a draft is read crudely: anything starting with "No" turns it down, anything else agrees.
        if (proposalPending && conversation.TrimEnd().Split('\n').Last().StartsWith("user: No", StringComparison.Ordinal))
            return Task.FromResult(new TicketInterview(false, Declined, null, null, null, null, null));

        return Task.FromResult(new TicketInterview(
            true, proposalPending ? Confirmation : Draft, "VPN client will not connect", "The user cannot connect to the corporate VPN.",
            "- User reported the VPN is down; they use the Contoso client.", "Check the user's VPN profile.", "Restarted the laptop",
            UserConfirmed: proposalPending));
    }
}

public sealed class RecordingPublisher : ITicketQueuePublisher
{
    public ConcurrentQueue<ProcessPrivateCorpTicketEvent> Sent { get; } = new();

    public Task PublishAsync(ProcessPrivateCorpTicketEvent ticketEvent, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(ticketEvent);
        return Task.CompletedTask;
    }
}

public sealed class NoKnowledgeBase : IKnowledgeBaseSearch
{
    public Task<IReadOnlyList<KbHit>> SearchAsync(string tenantId, string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<KbHit>>([]);
}

public sealed class StubSearch : ILiveSearchService
{
    public Task<string> GetLiveSearchResultsAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult("No incidents.");
}
