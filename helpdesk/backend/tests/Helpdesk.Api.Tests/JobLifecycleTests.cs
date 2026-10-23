using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Helpdesk.Api.Chat;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

/// <summary>The per-tenant limit on its own.</summary>
public class TenantJobLimiterTests
{
    private static TenantJobLimiter Limiter(int limit) => new(Options.Create(new ChatOptions { MaxJobsPerTenant = limit }));

    [Fact]
    public void ATenant_CannotHaveMoreInFlightThanTheLimit()
    {
        var limiter = Limiter(2);

        using var a = limiter.TryAcquire("t1");
        using var b = limiter.TryAcquire("t1");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(limiter.TryAcquire("t1"));
    }

    [Fact]
    public void OneTenantsLimit_DoesNotAffectAnother()
    {
        var limiter = Limiter(1);
        using var first = limiter.TryAcquire("t1");

        Assert.Null(limiter.TryAcquire("t1"));
        Assert.NotNull(limiter.TryAcquire("t2"));
    }

    [Fact]
    public void ReleasingALease_MakesRoom()
    {
        var limiter = Limiter(1);
        var lease = limiter.TryAcquire("t1")!;

        lease.Dispose();

        Assert.NotNull(limiter.TryAcquire("t1"));
    }

    [Fact]
    public void ReleasingTwice_DoesNotGiveBackAPlaceThatBelongsToSomeoneElse()
    {
        var limiter = Limiter(2);
        var first = limiter.TryAcquire("t1")!;
        using var second = limiter.TryAcquire("t1");

        first.Dispose();
        first.Dispose();

        Assert.Equal(1, limiter.InFlight("t1"));
    }

    [Fact]
    public void NothingIsKeptForATenantThatHasFinished()
    {
        var limiter = Limiter(3);
        limiter.TryAcquire("t1")!.Dispose();

        Assert.Equal(0, limiter.InFlight("t1"));
    }

    [Fact]
    public void ManyCallersAtOnce_NeverExceedTheLimit()
    {
        var limiter = Limiter(10);
        var leases = new ConcurrentBag<IDisposable>();

        Parallel.For(0, 200, _ =>
        {
            if (limiter.TryAcquire("busy-tenant") is { } lease) leases.Add(lease);
        });

        Assert.Equal(10, leases.Count);
        Assert.Equal(10, limiter.InFlight("busy-tenant"));
    }
}

/// <summary>The job queue: who may enqueue, and who can cancel what.</summary>
public class ChatJobQueueTests
{
    private static (ChatJobQueue Queue, TenantJobLimiter Tenants) Create(int perTenant = 10, int capacity = 100)
    {
        var options = Options.Create(new ChatOptions { MaxJobsPerTenant = perTenant, QueueCapacity = capacity });
        var tenants = new TenantJobLimiter(options);
        return (new ChatJobQueue(options, tenants), tenants);
    }

    private static ChatJob Job(string tenant = "t1", string user = "u1", Guid? conversation = null, string? connection = "c1") =>
        new(Guid.NewGuid(), tenant, user,
            new ChatMessageRequest { MessageText = "hi", ConversationId = conversation ?? Guid.NewGuid() }, connection);

    [Fact]
    public void AJob_TakesATenantPlace_UntilItIsCompleted()
    {
        var (queue, tenants) = Create();
        var job = Job();

        Assert.Equal(EnqueueResult.Accepted, queue.TryEnqueue(job));
        Assert.Equal(1, tenants.InFlight("t1"));

        queue.Complete(job);
        Assert.Equal(0, tenants.InFlight("t1"));
    }

    [Fact]
    public void CompletingTwice_IsHarmless()
    {
        var (queue, tenants) = Create();
        var job = Job();
        queue.TryEnqueue(job);

        queue.Complete(job);
        queue.Complete(job);

        Assert.Equal(0, tenants.InFlight("t1"));
    }

    [Fact]
    public void ATenantAtItsLimit_IsTurnedAway_ButAnotherTenantIsNot()
    {
        var (queue, _) = Create(perTenant: 1);
        queue.TryEnqueue(Job("t1", "alice"));

        Assert.Equal(EnqueueResult.TenantBusy, queue.TryEnqueue(Job("t1", "bob")));
        Assert.Equal(EnqueueResult.Accepted, queue.TryEnqueue(Job("t2", "carol")));
    }

    [Fact]
    public void ABusyConversation_IsReportedAsThat_EvenWhenTheTenantIsAtItsLimit()
    {
        var (queue, _) = Create(perTenant: 1);
        var conversation = Guid.NewGuid();
        queue.TryEnqueue(Job(conversation: conversation));

        Assert.Equal(EnqueueResult.AlreadyRunning, queue.TryEnqueue(Job(conversation: conversation)));
    }

    [Fact]
    public void ARejectedJob_DoesNotKeepATenantPlace()
    {
        var (queue, tenants) = Create(capacity: 1);
        queue.TryEnqueue(Job("t1", "alice"));

        Assert.Equal(EnqueueResult.Busy, queue.TryEnqueue(Job("t1", "bob")));
        Assert.Equal(1, tenants.InFlight("t1")); // only the accepted job
    }

    [Fact]
    public void CancellingAConnection_AbandonsOnlyThatConnectionsJobs()
    {
        var (queue, _) = Create();
        var mine1 = Job(connection: "mine");
        var mine2 = Job(user: "u2", connection: "mine");
        var other = Job(user: "u3", connection: "other");
        foreach (var job in new[] { mine1, mine2, other }) queue.TryEnqueue(job);

        var cancelled = queue.CancelConnection("mine");

        Assert.Equal(2, cancelled);
        Assert.True(mine1.IsAbandoned && mine2.IsAbandoned);
        Assert.False(other.IsAbandoned);
        Assert.Equal(0, queue.CancelConnection("mine")); // already abandoned: not counted again
    }

    [Fact]
    public void ACompletedJob_IsNoLongerCancellable()
    {
        var (queue, _) = Create();
        var job = Job();
        queue.TryEnqueue(job);
        queue.Complete(job);

        Assert.Equal(0, queue.CancelConnection("c1"));
        Assert.False(job.IsAbandoned);
    }

    [Fact]
    public void ACallerCanCancelOnlyTheirOwnConversation()
    {
        var (queue, _) = Create();
        var conversation = Guid.NewGuid();
        var job = Job("t1", "alice", conversation);
        queue.TryEnqueue(job);

        Assert.False(queue.CancelConversation("t1", "mallory", conversation));
        Assert.False(queue.CancelConversation("t2", "alice", conversation));
        Assert.False(queue.CancelConversation("t1", "alice", Guid.NewGuid()));
        Assert.False(job.IsAbandoned);

        Assert.True(queue.CancelConversation("t1", "alice", conversation));
        Assert.True(job.IsAbandoned);
        Assert.False(queue.CancelConversation("t1", "alice", conversation)); // nothing left to cancel
    }

    [Fact]
    public void AClosedQueue_AcceptsNothingMore_ButStillHoldsWhatWasQueued()
    {
        var (queue, tenants) = Create();
        var queued = Job();
        queue.TryEnqueue(queued);

        queue.CloseForWrites();

        Assert.Equal(EnqueueResult.Busy, queue.TryEnqueue(Job(user: "u2")));
        Assert.Equal(1, tenants.InFlight("t1")); // the refused job gave its place back
        Assert.True(queue.Reader.TryRead(out var read));
        Assert.Equal(queued, read);
    }
}

// ---------- A hub whose clients the test controls ----------

/// <summary>Records every event sent to a group. Can be told to never finish sending, like a client that is not reading.</summary>
internal sealed class FakeHub : IHubContext<ChatHub>
{
    public ConcurrentQueue<ChatStreamEvent> Events { get; } = new();
    public bool BlockFinalEvents { get; set; }

    public IHubClients Clients { get; }
    public IGroupManager Groups => throw new NotSupportedException();

    public FakeHub() => Clients = new Fan(this);

    public IEnumerable<ChatStreamEvent> For(string type) => Events.Where(e => e.Type == type);

    private sealed class Fan(FakeHub hub) : IHubClients
    {
        private readonly Proxy _proxy = new(hub);
        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class Proxy(FakeHub hub) : IClientProxy
    {
        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            var evt = (ChatStreamEvent)args[0]!;
            if (hub.BlockFinalEvents && evt.Type is ChatEventTypes.Completed or ChatEventTypes.Failed)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // only a token can end this, as with a stuck client
            }
            hub.Events.Enqueue(evt);
        }
    }
}

/// <summary>The processor and queue wired up by hand around fakes, so shutdown and cancellation are deterministic.</summary>
internal sealed class ProcessorRig : IAsyncDisposable
{
    public FakeHub Hub { get; } = new();
    public FakeInference Inference { get; } = new();
    public ChatJobQueue Queue { get; }
    public TenantJobLimiter Tenants { get; }
    public ChatJobProcessor Processor { get; }
    private readonly ServiceProvider _services;

    public ProcessorRig(int slots = 1, int perTenant = 10, int finalEventSeconds = 5)
    {
        var options = Options.Create(new ChatOptions
        {
            MaxConcurrentJobs = slots, MaxJobsPerTenant = perTenant, FinalEventTimeoutSeconds = finalEventSeconds,
            HeartbeatSeconds = 60, DeltaFlushMilliseconds = 0, JobTimeoutSeconds = 60
        });
        Tenants = new TenantJobLimiter(options);
        Queue = new ChatJobQueue(options, Tenants);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGuardrailRouterService>(new FakeRouter());
        services.AddSingleton<IPublicInferenceService>(Inference);
        services.AddSingleton<IIntakeFormGenerator>(new FakeIntakeFormGenerator());
        services.AddSingleton<ILiveSearchService>(new FakeSearch());
        services.AddSingleton<IKnowledgeBaseSearch>(new FakeKnowledgeBase());
        services.AddSingleton(Options.Create(new Helpdesk.Infrastructure.Kb.KbOptions()));
        services.AddSingleton<ITicketQueuePublisher>(new RecordingPublisher());
        services.AddSingleton<ISensitiveDataRedactor>(new SensitiveDataRedactor(Options.Create(new DlpOptions())));
        services.AddSingleton<IConversationSigner>(new ConversationSigner(
            Options.Create(new ConversationSigningOptions()), new DevEnvironment(), NullLogger<ConversationSigner>.Instance));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EscalationTokens>();
        services.AddScoped<ChatPipeline>();
        _services = services.BuildServiceProvider();

        Processor = new ChatJobProcessor(
            Queue, _services.GetRequiredService<IServiceScopeFactory>(), Hub, options, NullLogger<ChatJobProcessor>.Instance);
    }

    private sealed class DevEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    public ChatJob NewJob(string text, string tenant = "t1", string user = "u1", string? connection = "c1")
    {
        var job = new ChatJob(Guid.NewGuid(), tenant, user,
            new ChatMessageRequest { MessageText = text, ConversationId = Guid.NewGuid() }, connection);
        Assert.Equal(EnqueueResult.Accepted, Queue.TryEnqueue(job));
        return job;
    }

    public static async Task Eventually(Func<bool> condition, string because, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Gave up waiting for: " + because);
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
    }
}

/// <summary>Shutdown, abandonment and stuck clients, against the processor directly.</summary>
public class ChatJobProcessorTests
{
    // A job that runs until its token is cancelled, and says it has started.
    private static (Func<IChatProgress?, CancellationToken, Task> Reply, Func<string, bool> Started, ConcurrentBag<string> Cancelled)
        Blocking(FakeInference inference)
    {
        var started = new ConcurrentBag<string>();
        var cancelled = new ConcurrentBag<string>();
        return (async (_, ct) =>
        {
            var name = inference.Inputs.Last();
            started.Add(name);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.Add(name);
                throw;
            }
        }, name => started.Contains(name), cancelled);
    }

    [Fact]
    public async Task Shutdown_TellsRunningAndQueuedJobs_AndFreesTheirPlaces()
    {
        await using var rig = new ProcessorRig(slots: 1);
        var (reply, started, _) = Blocking(rig.Inference);
        rig.Inference.OnReply = reply;
        await rig.Processor.StartAsync(CancellationToken.None);

        var running = rig.NewJob("running");
        await ProcessorRig.Eventually(() => started("running"), "the first job to start");
        var queued1 = rig.NewJob("queued 1", user: "u2");
        var queued2 = rig.NewJob("queued 2", user: "u3");

        await rig.Processor.StopAsync(CancellationToken.None);

        var told = rig.Hub.For(ChatEventTypes.Failed).ToList();
        Assert.Contains(told, e => e.RequestId == running.RequestId && e.ErrorCode == "shutting_down");
        Assert.Contains(told, e => e.RequestId == queued1.RequestId && e.ErrorCode == "shutting_down");
        Assert.Contains(told, e => e.RequestId == queued2.RequestId && e.ErrorCode == "shutting_down");
        Assert.Equal(0, rig.Tenants.InFlight("t1"));
        Assert.False(rig.Processor.IsRunning);
        Assert.DoesNotContain("queued 1", rig.Inference.Inputs); // they never ran
    }

    [Fact]
    public async Task Shutdown_WhileAJobWaitsForASlot_DoesNotLoseThatJob()
    {
        // The job the loop has already taken off the queue, and is holding while it waits for a slot, used to be
        // dropped silently when the service stopped: its client waited for ever and its conversation stayed locked.
        await using var rig = new ProcessorRig(slots: 1);
        var (reply, started, _) = Blocking(rig.Inference);
        rig.Inference.OnReply = reply;
        await rig.Processor.StartAsync(CancellationToken.None);

        rig.NewJob("running");
        await ProcessorRig.Eventually(() => started("running"), "the first job to start");
        var waiting = rig.NewJob("waiting for a slot", user: "u2");
        var stillQueued = rig.NewJob("still queued", user: "u3");
        await ProcessorRig.Eventually(() => rig.Queue.Reader.Count == 1, "the second job to be taken off the queue");

        await rig.Processor.StopAsync(CancellationToken.None);

        var told = rig.Hub.For(ChatEventTypes.Failed).ToList();
        Assert.Contains(told, e => e.RequestId == waiting.RequestId && e.ErrorCode == "shutting_down");
        Assert.Contains(told, e => e.RequestId == stillQueued.RequestId && e.ErrorCode == "shutting_down");
        Assert.Equal(0, rig.Tenants.InFlight("t1"));
    }

    [Fact]
    public async Task ACancelledRunningJob_StopsTheModelCall_IsToldItWasCancelled_AndFreesItsPlace()
    {
        await using var rig = new ProcessorRig(slots: 1);
        var (reply, started, cancelled) = Blocking(rig.Inference);
        rig.Inference.OnReply = reply;
        await rig.Processor.StartAsync(CancellationToken.None);
        var job = rig.NewJob("work");
        await ProcessorRig.Eventually(() => started("work"), "the job to start");

        job.Abandon();

        await ProcessorRig.Eventually(() => cancelled.Contains("work"), "the model call to be cancelled");
        await ProcessorRig.Eventually(() => rig.Tenants.InFlight("t1") == 0, "the tenant place to be released");
        Assert.Contains(rig.Hub.For(ChatEventTypes.Failed), e => e.RequestId == job.RequestId && e.ErrorCode == "cancelled");
        await rig.Processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AJobWhoseClientLeftWhileItWasQueued_IsNeverStarted_AndTheNextOneRuns()
    {
        await using var rig = new ProcessorRig(slots: 1);
        var release = new TaskCompletionSource();
        rig.Inference.OnReply = async (_, ct) =>
        {
            if (rig.Inference.Inputs.Last() == "first") await release.Task.WaitAsync(ct);
        };
        await rig.Processor.StartAsync(CancellationToken.None);

        rig.NewJob("first");
        await ProcessorRig.Eventually(() => rig.Inference.Inputs.Contains("first"), "the first job to start");
        var leaving = rig.NewJob("client leaves", user: "u2", connection: "gone");
        var staying = rig.NewJob("client stays", user: "u3", connection: "here");
        rig.Queue.CancelConnection("gone");

        release.SetResult();

        await ProcessorRig.Eventually(() => rig.Hub.For(ChatEventTypes.Completed).Any(e => e.RequestId == staying.RequestId),
            "the remaining job to complete");
        Assert.DoesNotContain("client leaves", rig.Inference.Inputs); // no model call was spent on it
        Assert.DoesNotContain(rig.Hub.Events, e => e.RequestId == leaving.RequestId); // and nobody was messaged
        Assert.Equal(0, rig.Tenants.InFlight("t1"));
        await rig.Processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AJobAbandonedWhileWaitingForASlot_IsSkipped_WithoutBlockingTheLoop()
    {
        await using var rig = new ProcessorRig(slots: 1);
        var release = new TaskCompletionSource();
        rig.Inference.OnReply = async (_, ct) =>
        {
            if (rig.Inference.Inputs.Last() == "first") await release.Task.WaitAsync(ct);
        };
        await rig.Processor.StartAsync(CancellationToken.None);

        rig.NewJob("first");
        await ProcessorRig.Eventually(() => rig.Inference.Inputs.Contains("first"), "the first job to start");
        var waiting = rig.NewJob("waits then leaves", user: "u2");
        var next = rig.NewJob("next in line", user: "u3");
        await ProcessorRig.Eventually(() => rig.Queue.Reader.Count == 1, "the second job to be taken off the queue");

        waiting.Abandon(); // while the loop is blocked waiting for a slot for it

        // The slot is still taken by "first", yet the loop must move on to the next job rather than stay stuck.
        await ProcessorRig.Eventually(() => rig.Tenants.InFlight("t1") == 2, "the abandoned job to be released");
        release.SetResult();
        await ProcessorRig.Eventually(() => rig.Hub.For(ChatEventTypes.Completed).Any(e => e.RequestId == next.RequestId),
            "the next job to complete");
        Assert.DoesNotContain("waits then leaves", rig.Inference.Inputs);
        await rig.Processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AClientThatIsNotReading_CannotHoldAJobSlotOrBlockShutdown()
    {
        await using var rig = new ProcessorRig(slots: 1, finalEventSeconds: 1);
        rig.Hub.BlockFinalEvents = true; // sends of the last event never finish on their own
        await rig.Processor.StartAsync(CancellationToken.None);

        var stuck = rig.NewJob("answer, but nobody is reading");
        await ProcessorRig.Eventually(() => rig.Inference.Inputs.Contains("answer, but nobody is reading"), "the job to run");

        // Without a limit the slot and the tenant place would never be released.
        await ProcessorRig.Eventually(() => rig.Tenants.InFlight("t1") == 0, "the job to be released despite the stuck send", seconds: 6);
        Assert.DoesNotContain(rig.Hub.Events, e => e.RequestId == stuck.RequestId && e.Type == ChatEventTypes.Completed);

        var stopping = rig.Processor.StopAsync(CancellationToken.None);
        Assert.Same(stopping, await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task EveryOutcome_GivesTheTenantPlaceBack()
    {
        await using var rig = new ProcessorRig(slots: 2, perTenant: 2);
        rig.Inference.OnReply = (_, _) => throw new InvalidOperationException("model exploded");
        await rig.Processor.StartAsync(CancellationToken.None);

        for (var i = 0; i < 6; i++) // more jobs than the tenant limit: only possible if failed jobs release their place
        {
            var job = rig.NewJobWhenRoom($"fails {i}");
            await ProcessorRig.Eventually(() => rig.Hub.For(ChatEventTypes.Failed).Any(e => e.RequestId == job.RequestId), "the job to fail");
        }
        Assert.Equal(0, rig.Tenants.InFlight("t1"));
        await rig.Processor.StopAsync(CancellationToken.None);
    }
}

internal static class ProcessorRigExtensions
{
    /// <summary>Enqueues once the tenant has room, so a test can run more jobs than the tenant limit.</summary>
    public static ChatJob NewJobWhenRoom(this ProcessorRig rig, string text)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = new ChatJob(Guid.NewGuid(), "t1", "u1",
                new ChatMessageRequest { MessageText = text, ConversationId = Guid.NewGuid() }, "c1");
            if (rig.Queue.TryEnqueue(job) == EnqueueResult.Accepted) return job;
            Thread.Sleep(20);
        }
        throw new TimeoutException("The tenant never got room.");
    }
}

/// <summary>The same behaviour through the real hub and REST endpoint.</summary>
public class JobLifecycleEndToEndTests
{
    private static async Task<T> Within<T>(Task<T> task, string because, int seconds = 10)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task) throw new TimeoutException("Gave up waiting for: " + because);
        return await task;
    }

    private static async Task Within(Task task, string because, int seconds = 10) => await Within(task.ContinueWith(_ => true), because, seconds);

    [Fact]
    public async Task WhenTheClientDisconnects_TheRunningJobIsCancelled_AndTheConversationIsFreeAgain()
    {
        using var f = new ApiFactory();
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        var conversation = Guid.NewGuid();
        var client = await HubClient.ConnectAsync(f);
        await client.SendAsync("a long question", conversation);
        await Within(started.Task, "the job to start");

        await client.DisposeAsync(); // the browser closes

        await Within(cancelled.Task, "the model call to be cancelled after the client left");

        // The conversation can be used again straight away (the job released it).
        f.Inference.OnReply = null;
        await using var again = await HubClient.ConnectAsync(f);
        ChatAck ack;
        var until = DateTime.UtcNow.AddSeconds(5);
        do
        {
            ack = await again.SendAsync("next question", conversation);
            if (ack.RequestId is not null) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < until);
        Assert.NotNull(ack.RequestId);
    }

    [Fact]
    public async Task AnotherTabsJob_KeepsRunning_WhenOneConnectionCloses()
    {
        using var f = new ApiFactory();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var cancelled = false;
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            try { await release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        };
        await using var tabA = await HubClient.ConnectAsync(f);
        await using var tabB = await HubClient.ConnectAsync(f); // same user
        await tabA.SendAsync("question from tab A");
        await Within(started.Task, "the job to start");

        await tabB.DisposeAsync(); // closing the other tab must not cancel tab A's job
        await Task.Delay(300);
        release.SetResult();

        Assert.Equal(ChatEventTypes.Completed, (await tabA.UntilTerminalAsync())[^1].Type);
        Assert.False(cancelled);
    }

    [Fact]
    public async Task Cancel_StopsTheCallersOwnJob_AndTheirClientIsTold()
    {
        using var f = new ApiFactory();
        var started = new TaskCompletionSource();
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var conversation = Guid.NewGuid();
        await using var client = await HubClient.ConnectAsync(f);
        await client.SendAsync("work", conversation);
        await Within(started.Task, "the job to start");

        var cancelled = await client.Connection.InvokeAsync<bool>("Cancel", conversation);

        Assert.True(cancelled);
        var last = (await client.UntilTerminalAsync())[^1];
        Assert.Equal(ChatEventTypes.Failed, last.Type);
        Assert.Equal("cancelled", last.ErrorCode);
        Assert.False(await client.Connection.InvokeAsync<bool>("Cancel", conversation)); // nothing left to cancel
    }

    [Fact]
    public async Task Cancel_CannotStopSomeoneElsesJob()
    {
        using var f = new ApiFactory();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var conversation = Guid.NewGuid();
        await using var alice = await HubClient.ConnectAsync(f, user: "alice");
        await using var mallory = await HubClient.ConnectAsync(f, user: "mallory");
        await alice.SendAsync("alice's question", conversation);
        await Within(started.Task, "the job to start");

        Assert.False(await mallory.Connection.InvokeAsync<bool>("Cancel", conversation));
        release.SetResult();

        Assert.Equal(ChatEventTypes.Completed, (await alice.UntilTerminalAsync())[^1].Type);
    }

    [Fact]
    public async Task ATenantAtItsLimit_IsTurnedAwayOverTheHub_ButOtherTenantsAreNot_AndItRecovers()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Chat:MaxJobsPerTenant"] = "1" });
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        await using var alice = await HubClient.ConnectAsync(f, tenant: "tenant-a", user: "alice");
        await using var bob = await HubClient.ConnectAsync(f, tenant: "tenant-a", user: "bob");
        await using var carol = await HubClient.ConnectAsync(f, tenant: "tenant-b", user: "carol");

        var first = await alice.SendAsync("alice");
        await Within(started.Task, "the first job to start");
        var second = await bob.SendAsync("bob");
        var other = await carol.SendAsync("carol");

        Assert.NotNull(first.RequestId);
        Assert.Equal("tenant_busy", second.ErrorCode);
        Assert.NotNull(other.RequestId); // another tenant is unaffected

        release.SetResult();
        await alice.UntilTerminalAsync();
        var retry = await Retry(() => bob.SendAsync("bob again"));
        Assert.NotNull(retry.RequestId);
    }

    [Fact]
    public async Task ABusyConversation_StillSaysInProgress_NotTenantBusy()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Chat:MaxJobsPerTenant"] = "1" });
        var started = new TaskCompletionSource();
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var conversation = Guid.NewGuid();
        await using var client = await HubClient.ConnectAsync(f);
        await client.SendAsync("one", conversation);
        await Within(started.Task, "the job to start");

        Assert.Equal("in_progress", (await client.SendAsync("two", conversation)).ErrorCode);
    }

    private static async Task<ChatAck> Retry(Func<Task<ChatAck>> send)
    {
        ChatAck ack;
        var until = DateTime.UtcNow.AddSeconds(5);
        do
        {
            ack = await send();
            if (ack.RequestId is not null) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < until);
        return ack;
    }

    // ----- REST fallback -----

    private static HttpRequestMessage Post(string tenant, string user)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/message")
        {
            Content = JsonContent.Create(new { messageText = "excel xlookup", conversationId = Guid.NewGuid() })
        };
        req.Headers.Add("X-Dev-Tenant", tenant);
        req.Headers.Add("X-Dev-User", user);
        return req;
    }

    [Fact]
    public async Task Rest_IsHeldToTheSameTenantLimit_AndRecovers()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Chat:MaxJobsPerTenant"] = "1" });
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var client = f.CreateClient();

        var first = client.SendAsync(Post("tenant-a", "alice"));
        await Within(started.Task, "the first request to start");
        var refused = await client.SendAsync(Post("tenant-a", "bob"));
        var otherTenant = client.SendAsync(Post("tenant-b", "carol"));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));

        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherTenant).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("tenant-a", "bob"))).StatusCode);
    }

    [Fact]
    public async Task Rest_AFailedRequest_GivesItsPlaceBack()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Chat:MaxJobsPerTenant"] = "1" });
        f.Router.Handler = _ => throw new GuardrailClassificationException("down");
        var client = f.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            // Would be 429 from the second request on if a failure kept the tenant's place.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(Post("tenant-a", "alice"))).StatusCode);
        }
    }

    [Fact]
    public async Task Rest_WhenTheCallerGivesUp_TheWorkIsCancelled_AndThePlaceIsFree()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Chat:MaxJobsPerTenant"] = "1" });
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        f.Inference.OnReply = async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        var client = f.CreateClient();
        using var giveUp = new CancellationTokenSource();

        var request = client.SendAsync(Post("tenant-a", "alice"), giveUp.Token);
        await Within(started.Task, "the request to start");
        giveUp.Cancel();

        await Within(cancelled.Task, "the model call to be cancelled when the caller left");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        f.Inference.OnReply = null;
        var until = DateTime.UtcNow.AddSeconds(5);
        HttpStatusCode status;
        do
        {
            status = (await client.SendAsync(Post("tenant-a", "bob"))).StatusCode;
            if (status == HttpStatusCode.OK) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < until);
        Assert.Equal(HttpStatusCode.OK, status);
    }
}
