namespace Helpdesk.Infrastructure.Attachments;

public static class AttachmentProviders
{
    /// <summary>Kept in process memory: for local development and tests only. Files are lost on restart and AgentDesk cannot see them.</summary>
    public const string InMemory = "InMemory";

    /// <summary>Azure Blob Storage, in the container shared with AgentDesk.</summary>
    public const string Azure = "Azure";
}

/// <summary>Settings for chat attachments, bound from <c>Attachments</c>. Every limit can be changed.</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";

    /// <summary>
    /// <c>InMemory</c> or <c>Azure</c>. Empty means <c>InMemory</c> in Development and attachments switched off elsewhere,
    /// so a production deployment never keeps files in memory by accident.
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>Key-based connection string (Azurite: <c>UseDevelopmentStorage=true</c>).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Blob service URI for Entra ID auth (managed identity).</summary>
    public string? ServiceUri { get; set; }

    /// <summary>The container AgentDesk reads attachments from (its <c>Blobs:Container</c>). Both must name the same one on the same account.</summary>
    public string Container { get; set; } = "ticket-attachments";

    public bool CreateContainerIfNotExists { get; set; } = true;

    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Claude rejects images over 5 MB, so images have their own, lower, limit.</summary>
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    public int MaxFilesPerMessage { get; set; } = 5;
    public int MaxFilesPerConversation { get; set; } = 20;
    public long MaxTotalBytesPerConversation { get; set; } = 25 * 1024 * 1024;

    /// <summary>The most text of one file shown to the model for the current message. Longer files are reduced, not cut at the end.</summary>
    public int MaxExtractedTokensPerFile { get; set; } = 20_000;

    /// <summary>The most text of one file shown to the model when it was attached to an earlier message.</summary>
    public int MaxEarlierTokensPerFile { get; set; } = 1_500;

    /// <summary>The most file text, in tokens, shown to the model across all files in one request.</summary>
    public int MaxTokensPerRequest { get; set; } = 40_000;

    /// <summary>What an image or scanned PDF is assumed to cost in input tokens, when choosing the model tier.</summary>
    public int ImageTokenEstimate { get; set; } = 1_500;

    /// <summary>The browser turns pasted text into an attachment at this many characters or lines, or sooner if it looks like a log.</summary>
    public int PasteMinChars { get; set; } = 1_500;
    public int PasteMinLines { get; set; } = 25;

    /// <summary>Files from conversations that never raised a ticket are deleted after this long.</summary>
    public int RetentionHours { get; set; } = 24;

    public int CleanupIntervalMinutes { get; set; } = 30;

    /// <summary>Guards against zip bombs and huge documents when reading Office files and PDFs.</summary>
    public long MaxOfficeUncompressedBytes { get; set; } = 50L * 1024 * 1024;
    public int MaxOfficeEntries { get; set; } = 1_000;
    public int MaxPdfPages { get; set; } = 200;

    /// <summary>The most extracted text kept for one file, in characters.</summary>
    public int MaxExtractedChars { get; set; } = 2_000_000;

    /// <summary>
    /// Extensions that may be uploaded, with the leading dot. Empty (the default) allows everything supported
    /// (<see cref="AttachmentTypes.SupportedExtensions"/>); list fewer to narrow it. Unsupported ones are ignored and reported by /status.
    /// It is empty by default, and not pre-filled, because configuration binding merges a list into its default instead of replacing it.
    /// </summary>
    public List<string> AllowedExtensions { get; set; } = [];

    /// <summary>The extensions in force: the configured ones that are supported, or every supported one when none are configured.</summary>
    public IReadOnlyList<string> EffectiveExtensions()
    {
        var configured = AllowedExtensions.Select(e => e.Trim().ToLowerInvariant()).Where(AttachmentTypes.IsSupported).Distinct().Order().ToList();
        return AllowedExtensions.Count == 0 ? AttachmentTypes.SupportedExtensions : configured;
    }

    /// <summary>The provider after applying the default for the environment, or null when attachments are off.</summary>
    public string? EffectiveProvider(bool development) =>
        string.IsNullOrWhiteSpace(Provider) ? (development ? AttachmentProviders.InMemory : null) : Provider.Trim();
}
