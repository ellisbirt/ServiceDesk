using System.Collections.Concurrent;
using System.Diagnostics;
using Helpdesk.Core;
using Helpdesk.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Runs queued chat jobs outside the hub invocation, so a slow or rate-limited model call never holds a request
/// open. Each job streams progress to the user's SignalR group and ends with exactly one <c>completed</c> or
/// <c>failed</c> event. A heartbeat event is sent every few seconds in between.
/// <para>
/// Every job ends one way or another, and every wait is cancellable. A job stops when it completes, fails, times out,
/// when the service stops, or when nobody is waiting for it any more (<see cref="ChatJob.Abandoned"/>: the client's
/// connection closed, or it asked to cancel). A job still queued when the service stops is told so rather than
/// being dropped silently, and a job whose client has already left is never started.
/// </para>
/// </summary>
public sealed class ChatJobProcessor(
    ChatJobQueue queue,
    IServiceScopeFactory scopes,
    IHubContext<ChatHub> hub,
    IOptions<ChatOptions> options,
    ILogger<ChatJobProcessor> logger) : BackgroundService
{
    private readonly ChatOptions _options = options.Value;
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        Volatile.Write(ref _isRunning, 1);
        using var slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentJobs));
        try
        {
            try
            {
                await foreach (var job in queue.Reader.ReadAllAsync(stopping))
                {
                    // The client left while the job was queued: do not spend a model call on it.
                    if (job.IsAbandoned)
                    {
                        await EndWithoutRunningAsync(job, shuttingDown: false);
                        continue;
                    }

                    if (!await TryAcquireSlotAsync(slots, job, stopping))
                    {
                        // Cancelled while waiting for a free slot: because the service is stopping, or the client left.
                        await EndWithoutRunningAsync(job, shuttingDown: stopping.IsCancellationRequested);
                        if (stopping.IsCancellationRequested) break;
                        continue;
                    }

                    var task = RunJobAsync(job, slots, stopping);
                    _running.TryAdd(task, 0);
                    _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Shutting down: in-flight jobs see the same token and report failure to their clients.
            }

            // Anything still waiting in the queue will never run. Say so, so those clients do not wait for ever,
            // and free their conversations and the tenants' places.
            queue.CloseForWrites();
            while (queue.Reader.TryRead(out var waiting))
            {
                await EndWithoutRunningAsync(waiting, shuttingDown: true);
            }

            await Task.WhenAll(_running.Keys);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    /// <summary>Waits for a free job slot, giving up if the service stops or the client leaves in the meantime.</summary>
    private static async Task<bool> TryAcquireSlotAsync(SemaphoreSlim slots, ChatJob job, CancellationToken stopping)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stopping, job.Abandoned);
        try
        {
            await slots.WaitAsync(waiting.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Ends a job that was taken off the queue but never started.</summary>
    private async Task EndWithoutRunningAsync(ChatJob job, bool shuttingDown)
    {
        Telemetry.ChatJobs.Add(1, new KeyValuePair<string, object?>("outcome", shuttingDown ? "cancelled" : "abandoned"));
        try
        {
            // Nobody is listening for an abandoned job, and a client that asked to cancel already knows.
            if (!shuttingDown || job.IsAbandoned)
            {
                logger.LogInformation("Skipped queued chat request {RequestId}: the client is no longer waiting", job.RequestId);
                return;
            }

            logger.LogWarning("Chat request {RequestId} was still queued when the service stopped", job.RequestId);
            var progress = NewProgress(job);
            await SendWithinLimitAsync(job, "failed", token => progress.SendAsync(
                progress.Event(ChatEventTypes.Failed) with
                {
                    Error = "The service is restarting. Please send your message again.",
                    ErrorCode = "shutting_down"
                }, token));
        }
        finally
        {
            queue.Complete(job);
        }
    }

    private async Task RunJobAsync(ChatJob job, SemaphoreSlim slots, CancellationToken stopping)
    {
        var progress = NewProgress(job);
        var clock = Stopwatch.StartNew();

        // Stops when the service stops, the job times out, or nobody is waiting for the result any more.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stopping, job.Abandoned);
        jobCts.CancelAfter(TimeSpan.FromSeconds(_options.JobTimeoutSeconds));
        var heartbeat = HeartbeatAsync(progress, clock, jobCts.Token);

        // The request id the client got in its ack is the correlation id for everything this job logs and traces.
        var correlationId = job.RequestId.ToString();
        using var activity = Telemetry.Source.StartActivity("chat.job");
        activity?.SetTag("helpdesk.correlation_id", correlationId);
        activity?.SetTag("helpdesk.tenant_id", job.TenantId);
        activity?.SetTag("helpdesk.conversation_id", job.Request.ConversationId);
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = activity?.TraceId.ToString() ?? "",
            ["TenantId"] = job.TenantId
        });
        var outcome = "completed";

        try
        {
            await SendWithinLimitAsync(job, "started", token => progress.SendAsync(progress.Event(ChatEventTypes.Started), token));

            await using var scope = scopes.CreateAsyncScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<ChatPipeline>();
            var response = await pipeline.RunAsync(
                job.Request, job.TenantId, job.UserId, progress, jobCts.Token, job.UserAgent);

            await SendWithinLimitAsync(job, "flush", token => progress.FlushAsync(token).AsTask());
            await SendWithinLimitAsync(job, "completed", token =>
                progress.SendAsync(progress.Event(ChatEventTypes.Completed) with { Response = response }, token));
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && jobCts.IsCancellationRequested;
            outcome = cancelled
                ? (stopping.IsCancellationRequested ? "cancelled" : job.IsAbandoned ? "abandoned" : "timeout")
                : "failed";
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
            await ReportFailureAsync(progress, job, ex, outcome, cancelled);
        }
        finally
        {
            Telemetry.ChatJobs.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
            Telemetry.ChatJobDuration.Record(clock.Elapsed.TotalSeconds);
            await jobCts.CancelAsync();
            await heartbeat;
            queue.Complete(job);
            slots.Release();
        }
    }

    private async Task ReportFailureAsync(HubChatProgress progress, ChatJob job, Exception ex, string outcome, bool cancelled)
    {
        string title, code;
        if (cancelled)
        {
            (title, code) = outcome switch
            {
                "cancelled" => ("The service is restarting. Please send your message again.", "shutting_down"),
                "abandoned" => ("The request was cancelled.", "cancelled"),
                _ => ("This is taking too long. Please try again later.", "timeout")
            };
            logger.LogWarning("Chat request {RequestId} cancelled: {Code}", job.RequestId, code);
        }
        else
        {
            var mapped = ApiErrors.Map(ex);
            (title, code) = (mapped.Title, mapped.Code);
            if (mapped.Status >= 500) logger.LogError(ex, "Chat request {RequestId} failed with {Code}", job.RequestId, code);
            else logger.LogWarning(ex, "Chat request {RequestId} rejected with {Code}", job.RequestId, code);
        }

        await SendWithinLimitAsync(job, "failed", token => progress.SendAsync(
            progress.Event(ChatEventTypes.Failed) with
            {
                Error = title,
                ErrorCode = code,
                RetryAfterSeconds = cancelled ? null : ApiErrors.RetryAfterSeconds(ex)
            }, token));
    }

    /// <summary>
    /// Hands an event to SignalR, but only waits <see cref="ChatOptions.FinalEventTimeoutSeconds"/> for it. These
    /// sends deliberately do not use the job's own token (it is already cancelled when a job is being ended), but
    /// they must not wait for ever either: a client that is not reading would hold a job slot and block shutdown.
    /// </summary>
    private async Task SendWithinLimitAsync(ChatJob job, string what, Func<CancellationToken, Task> send)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.FinalEventTimeoutSeconds)));
        try
        {
            await send(limit.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Could not hand the {Event} event of chat request {RequestId} to SignalR within {Seconds}s; the client will not see it",
                what, job.RequestId, _options.FinalEventTimeoutSeconds);
        }
    }

    private HubChatProgress NewProgress(ChatJob job) =>
        new(hub, job, TimeSpan.FromMilliseconds(_options.DeltaFlushMilliseconds), logger);

    /// <summary>Tells the client the job is alive, e.g. while it sits in a long rate-limit wait.</summary>
    private async Task HeartbeatAsync(HubChatProgress progress, Stopwatch clock, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await progress.SendAsync(
                    progress.Event(ChatEventTypes.Heartbeat) with { ElapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1) }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Job finished.
        }
    }
}
