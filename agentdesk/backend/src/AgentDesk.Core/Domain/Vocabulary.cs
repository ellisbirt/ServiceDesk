namespace AgentDesk.Core.Domain;

/// <summary>Document discriminator stored in <c>docType</c>. Several kinds share a container and partition.</summary>
public static class DocTypes
{
    public const string Ticket = "ticket";
    public const string Comment = "comment";
    public const string Event = "event";
    public const string Counter = "counter";
    public const string IntakeReceipt = "intakeReceipt";
    public const string KbArticle = "kbArticle";
    public const string KbVersion = "kbVersion";
    public const string Attachment = "attachment";
}

public static class TicketStatuses
{
    public const string New = "new";
    public const string Open = "open";
    public const string Pending = "pending";
    public const string OnHold = "on_hold";
    public const string Resolved = "resolved";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [New, Open, Pending, OnHold, Resolved, Closed, Cancelled];

    /// <summary>States in which the ticket still needs work (and its SLA clock matters).</summary>
    public static readonly IReadOnlySet<string> Active = new HashSet<string> { New, Open, Pending, OnHold };
}

/// <summary>Why a ticket is waiting. Required when moving to <see cref="TicketStatuses.Pending"/>.</summary>
public static class PendingReasons
{
    public const string AwaitingRequester = "awaiting_requester";
    public const string AwaitingVendor = "awaiting_vendor";
    public const string AwaitingChange = "awaiting_change";
    public const string AwaitingApproval = "awaiting_approval";

    public static readonly IReadOnlyList<string> All = [AwaitingRequester, AwaitingVendor, AwaitingChange, AwaitingApproval];
}

public static class TicketTypes
{
    public const string Incident = "incident";
    public const string ServiceRequest = "service_request";
    public const string Question = "question";
    public const string Problem = "problem";

    public static readonly IReadOnlyList<string> All = [Incident, ServiceRequest, Question, Problem];

    public static string NumberPrefix(string type) => type switch
    {
        ServiceRequest => "REQ",
        Question => "QST",
        Problem => "PRB",
        _ => "INC"
    };
}

public static class Channels
{
    public const string ChatIntake = "chat_intake";
    public const string Portal = "portal";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Agent = "agent";

    public static readonly IReadOnlyList<string> All = [ChatIntake, Portal, Email, Phone, Agent];
}

public static class Priorities
{
    public const string P1 = "P1";
    public const string P2 = "P2";
    public const string P3 = "P3";
    public const string P4 = "P4";

    public static readonly IReadOnlyList<string> All = [P1, P2, P3, P4];
}

/// <summary>Categories aligned with the intake classifier's categories (see evals/system-prompt.md).</summary>
public static class TicketCategories
{
    public const string Hardware = "hardware";
    public const string Software = "software";
    public const string Access = "access";
    public const string Network = "network";
    public const string Email = "email_collaboration";
    public const string Security = "security";
    public const string Performance = "performance";
    public const string Outage = "outage";
    public const string Peripherals = "printer_peripheral";
    public const string Information = "information";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        [Hardware, Software, Access, Network, Email, Security, Performance, Outage, Peripherals, Information, Other];
}

public static class ResolutionCodes
{
    public const string Solved = "solved";
    public const string Workaround = "workaround";
    public const string KnownError = "known_error";
    public const string Duplicate = "duplicate";
    public const string NotReproducible = "not_reproducible";
    public const string UserResolved = "user_resolved";
    public const string NoResponse = "no_response";

    public static readonly IReadOnlyList<string> All =
        [Solved, Workaround, KnownError, Duplicate, NotReproducible, UserResolved, NoResponse];
}

public static class Visibilities
{
    /// <summary>Visible to the requester (and sent to them once a delivery channel exists).</summary>
    public const string Public = "public";
    /// <summary>Agents only.</summary>
    public const string Internal = "internal";

    public static readonly IReadOnlyList<string> All = [Public, Internal];
}

public static class KbStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";

    public static readonly IReadOnlyList<string> All = [Draft, Published, Archived];
}

public static class EmbeddingStatuses
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Failed = "failed";
    /// <summary>No embedding role is configured, so nothing is generated.</summary>
    public const string Disabled = "disabled";
}

public static class AttachmentStatuses
{
    public const string PendingUpload = "pending_upload";
    public const string Available = "available";
    public const string Quarantined = "quarantined";
    public const string Rejected = "rejected";
}

/// <summary>Audit event names recorded on the ticket timeline.</summary>
public static class TicketEventTypes
{
    public const string Created = "created";
    public const string StatusChanged = "status_changed";
    public const string Assigned = "assigned";
    public const string FieldsChanged = "fields_changed";
    public const string KbCited = "kb_cited";
    public const string KbPublished = "kb_published";
    public const string AttachmentAdded = "attachment_added";
}
