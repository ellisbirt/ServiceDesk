namespace Helpdesk.Api.Chat;

public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>Jobs processed at once. Jobs mostly wait on the network, so this can be well above the core count.</summary>
    public int MaxConcurrentJobs { get; set; } = 32;

    /// <summary>Accepted-but-not-started jobs held in memory; beyond this the hub rejects with "busy".</summary>
    public int QueueCapacity { get; set; } = 200;

    /// <summary>
    /// Requests one tenant may have in flight at once (queued or running, hub and REST together). Beyond it the
    /// tenant is told to retry, so one busy tenant cannot take every job slot and fill the queue for the others.
    /// A large tenant may need a higher value; keep it below <see cref="MaxConcurrentJobs"/> plus
    /// <see cref="QueueCapacity"/> or it protects nobody.
    /// </summary>
    public int MaxJobsPerTenant { get; set; } = 10;

    /// <summary>
    /// How long to wait for the last event of a job (completed, failed) to be handed to SignalR. A client that is
    /// not reading must not hold a job slot, or block shutdown, forever.
    /// </summary>
    public int FinalEventTimeoutSeconds { get; set; } = 5;

    /// <summary>Gap between application-level heartbeat events while a job is running.</summary>
    public int HeartbeatSeconds { get; set; } = 5;

    /// <summary>Hard ceiling for one job, including any rate-limit waits.</summary>
    public int JobTimeoutSeconds { get; set; } = 600;

    /// <summary>Streamed text is sent in batches at most this often, to avoid one hub message per token.</summary>
    public int DeltaFlushMilliseconds { get; set; } = 50;

    /// <summary>SignalR transport keep-alive ping interval. The client timeout must stay at least twice this.</summary>
    public int KeepAliveSeconds { get; set; } = 15;

    public int ClientTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Largest single hub message the server accepts (SignalR's own default, 32 KB). Requests bigger than the
    /// UI's single-message threshold are uploaded in parts, so this never needs to cover a whole conversation;
    /// it must be at least <see cref="ChatRequestValidator.MaxPartBytes"/>.
    /// </summary>
    public int MaxReceiveMessageBytes { get; set; } = 32 * 1024;
}
