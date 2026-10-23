using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using AgentDesk.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentDesk.Core.Services;

public sealed class TicketOptions
{
    public const string Section = "Tickets";

    public string? DefaultAssignmentGroup { get; set; }
    /// <summary>How long intake receipts are kept for queue de-duplication.</summary>
    public int IntakeReceiptTtlSeconds { get; set; } = (int)TimeSpan.FromDays(14).TotalSeconds;
    /// <summary>Use a per-type number prefix (INC/REQ/QST/PRB). When false every ticket uses INC.</summary>
    public bool PrefixByType { get; set; } = true;
    public SlaOptions Sla { get; set; } = new();
}

/// <summary>Intake ticket details, mapped from the queue event by the consumer.</summary>
public sealed record IntakeTicket(
    string TenantId,
    string EventId,
    string RequesterId,
    string Subject,
    string Description,
    string? Severity,
    string? Category,
    string? ConversationId,
    string? CorrelationId,
    string? ConversationSummary = null);

public sealed class TicketService(
    ITicketRepository repository,
    IAttachmentRepository attachments,
    KbService kb,
    ITicketNotifier notifier,
    IClock clock,
    IOptions<TicketOptions> options,
    ILogger<TicketService> logger)
{
    public const string CounterName = "ticket";
    public const int MaxSubject = 200;
    public const int MaxDescription = 20_000;
    public const int MaxComment = 20_000;
    private const int MaxConflictRetries = 3;

    private readonly SlaCalculator _sla = new(options.Value.Sla);

    public async Task<Ticket> GetAsync(CurrentUser user, string id, CancellationToken ct = default)
    {
        var ticket = await repository.GetAsync(user.TenantId, id, ct) ?? throw new NotFoundException("Ticket", id);
        SlaCalculator.Evaluate(ticket, clock.UtcNow);
        return ticket;
    }

    public async Task<Page<Ticket>> QueryAsync(CurrentUser user, TicketQuery query, CancellationToken ct = default)
    {
        var v = new Validator()
            .OneOf("priority", query.Priority, Priorities.All, optional: true)
            .OneOf("sort", query.Sort, ["updatedAt", "createdAt", "priority", "dueAt"])
            .Range("pageSize", query.PageSize, 1, 100);
        foreach (var status in query.Statuses ?? []) v.OneOf("status", status, TicketStatuses.All);
        v.ThrowIfInvalid();

        var page = await repository.QueryAsync(user.TenantId, query, ct);
        var now = clock.UtcNow;
        foreach (var ticket in page.Items) SlaCalculator.Evaluate(ticket, now);
        return page;
    }

    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(CurrentUser user, string id, CancellationToken ct = default)
    {
        await GetAsync(user, id, ct);
        return await repository.GetTimelineAsync(user.TenantId, id, ct);
    }

    public async Task<Ticket> CreateAsync(CurrentUser user, CreateTicketCommand command, CancellationToken ct = default)
    {
        ValidateCreate(command);
        var ticket = await NewTicketAsync(user, TenantDocument.NewId(), command, ct);
        return await PersistNewAsync(user, ticket, ct);
    }

    /// <summary>
    /// Creates a ticket from an intake queue event exactly once. The ticket id is derived from the event id, and a receipt is
    /// written first, so a redelivered message (even after a crash between the two writes) never creates a duplicate.
    /// </summary>
    /// <returns>The ticket and whether this call created it.</returns>
    public async Task<(Ticket Ticket, bool Created)> ImportFromIntakeAsync(IntakeTicket intake, CancellationToken ct = default)
    {
        var system = CurrentUser.System(intake.TenantId);
        var ticketId = DeterministicId(intake.TenantId, intake.EventId);

        var receipt = new IntakeReceipt
        {
            Id = IntakeReceipt.IdFor(intake.EventId),
            TenantId = intake.TenantId,
            EventId = intake.EventId,
            TicketId = ticketId,
            CreatedAt = clock.UtcNow,
            CreatedBy = system.UserId,
            UpdatedAt = clock.UtcNow,
            UpdatedBy = system.UserId,
            Ttl = options.Value.IntakeReceiptTtlSeconds > 0 ? options.Value.IntakeReceiptTtlSeconds : null
        };
        if (!await repository.TryCreateIntakeReceiptAsync(receipt, ct))
        {
            var prior = await repository.GetIntakeReceiptAsync(intake.TenantId, intake.EventId, ct);
            ticketId = prior?.TicketId ?? ticketId;
            if (await repository.GetAsync(intake.TenantId, ticketId, ct) is { } existing) return (existing, false);
        }

        var (impact, urgency) = PriorityMatrix.FromSeverity(intake.Severity);
        var command = new CreateTicketCommand
        {
            Subject = Truncate(string.IsNullOrWhiteSpace(intake.Subject) ? "Support request" : intake.Subject, MaxSubject),
            Description = Truncate(intake.Description, MaxDescription),
            Type = TicketTypes.Incident,
            Impact = impact,
            Urgency = urgency,
            Category = intake.Category is { } c && TicketCategories.All.Contains(c) ? c : null,
            Requester = new Person(intake.RequesterId, intake.RequesterId),
            Channel = Channels.ChatIntake,
            Source = new TicketSource(intake.EventId, intake.ConversationId, intake.CorrelationId)
        };
        var ticket = await NewTicketAsync(system, ticketId, command, ct);
        ticket.ConversationSummary = string.IsNullOrWhiteSpace(intake.ConversationSummary)
            ? null
            : Truncate(intake.ConversationSummary.Trim(), MaxDescription);
        try
        {
            return (await PersistNewAsync(system, ticket, ct), true);
        }
        catch (DuplicateException)
        {
            var existing = await repository.GetAsync(intake.TenantId, ticketId, ct)
                ?? throw new InvalidOperationException($"Ticket {ticketId} reported as duplicate but could not be read.");
            return (existing, false);
        }
    }

    public Task<Ticket> UpdateAsync(CurrentUser user, string id, UpdateTicketCommand command, string? ifMatch, CancellationToken ct = default)
    {
        new Validator()
            .When(command.Subject is not null && string.IsNullOrWhiteSpace(command.Subject), "subject", "cannot be empty.")
            .MaxLength("subject", command.Subject, MaxSubject)
            .MaxLength("description", command.Description, MaxDescription)
            .OneOf("type", command.Type, TicketTypes.All, optional: true)
            .Range("impact", command.Impact, 1, 3)
            .Range("urgency", command.Urgency, 1, 3)
            .OneOf("category", command.Category, TicketCategories.All, optional: true)
            .MaxLength("subcategory", command.Subcategory, 100)
            .When(command.Tags is { Count: > 20 }, "tags", "must contain at most 20 tags.")
            .ThrowIfInvalid();

        return MutateAsync(user, id, ifMatch, ticket =>
        {
            if (TicketStateMachine.IsTerminal(ticket.Status))
                throw new InvalidTransitionException($"A {ticket.Status} ticket cannot be edited.");

            var changes = new List<FieldChange>();
            Set("subject", ticket.Subject, command.Subject?.Trim(), v => ticket.Subject = v!);
            Set("description", ticket.Description, command.Description, v => ticket.Description = v!, recordValues: false);
            Set("type", ticket.Type, command.Type, v => ticket.Type = v!);
            Set("impact", ticket.Impact.ToString(), command.Impact?.ToString(), v => ticket.Impact = int.Parse(v!));
            Set("urgency", ticket.Urgency.ToString(), command.Urgency?.ToString(), v => ticket.Urgency = int.Parse(v!));
            Set("category", ticket.Category, command.Category, v => ticket.Category = v);
            Set("subcategory", ticket.Subcategory, command.Subcategory, v => ticket.Subcategory = v);
            if (command.Tags is not null)
            {
                var tags = command.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
                Set("tags", string.Join(",", ticket.Tags), string.Join(",", tags), _ => ticket.Tags = tags);
            }
            if (command.Watchers is not null) ticket.Watchers = command.Watchers.DistinctBy(w => w.Id).ToList();

            var priority = PriorityMatrix.Compute(ticket.Impact, ticket.Urgency);
            if (priority != ticket.Priority)
            {
                changes.Add(new FieldChange("priority", ticket.Priority, priority));
                ticket.Priority = priority;
                _sla.Apply(ticket, clock.UtcNow);
            }
            return changes.Count == 0 ? [] : [NewEvent(ticket, user, TicketEventTypes.FieldsChanged, changes)];

            void Set(string field, string? current, string? next, Action<string?> apply, bool recordValues = true)
            {
                if (next is null || next == current) return;
                changes.Add(recordValues ? new FieldChange(field, current, next) : new FieldChange(field, null, null));
                apply(next);
            }
        }, ct);
    }

    public Task<Ticket> AssignAsync(CurrentUser user, string id, AssignTicketCommand command, string? ifMatch, CancellationToken ct = default)
    {
        new Validator()
            .MaxLength("assignmentGroup", command.AssignmentGroup, 100)
            .When(command.Assignee is { } a && (string.IsNullOrWhiteSpace(a.Id) || a.Id.Contains(':')), "assignee.id", "is invalid.")
            .ThrowIfInvalid();

        return MutateAsync(user, id, ifMatch, ticket =>
        {
            if (TicketStateMachine.IsTerminal(ticket.Status))
                throw new InvalidTransitionException($"A {ticket.Status} ticket cannot be reassigned.");
            var changes = new List<FieldChange>();
            var group = string.IsNullOrWhiteSpace(command.AssignmentGroup) ? null : command.AssignmentGroup.Trim();
            if (group != ticket.AssignmentGroup)
            {
                changes.Add(new FieldChange("assignmentGroup", ticket.AssignmentGroup, group));
                ticket.AssignmentGroup = group;
            }
            if (command.Assignee?.Id != ticket.Assignee?.Id)
            {
                changes.Add(new FieldChange("assignee", ticket.Assignee?.DisplayName, command.Assignee?.DisplayName));
                ticket.Assignee = command.Assignee;
            }
            return changes.Count == 0 ? [] : [NewEvent(ticket, user, TicketEventTypes.Assigned, changes)];
        }, ct);
    }

    public Task<Ticket> TransitionAsync(CurrentUser user, string id, TransitionTicketCommand command, string? ifMatch, CancellationToken ct = default)
    {
        ValidateTransition(command);
        return MutateAsync(user, id, ifMatch, ticket => ApplyTransition(ticket, user, command), ct);
    }

    public async Task<CommentResult> AddCommentAsync(CurrentUser user, string id, AddCommentCommand command, CancellationToken ct = default)
    {
        new Validator()
            .OneOf("visibility", command.Visibility, Visibilities.All)
            .Required("body", command.Body, MaxComment)
            .When(command.AttachmentIds is { Count: > 10 }, "attachmentIds", "must contain at most 10 attachments.")
            .When(command.CitedKbArticleIds is { Count: > 10 }, "citedKbArticleIds", "must contain at most 10 articles.")
            .ThrowIfInvalid();
        if (command.Transition is not null) ValidateTransition(command.Transition);

        var ticket = await GetAsync(user, id, ct);
        var isPublic = command.Visibility == Visibilities.Public;
        if (TicketStateMachine.IsTerminal(ticket.Status) && isPublic)
            throw new InvalidTransitionException($"A {ticket.Status} ticket cannot receive public replies. Add an internal note instead.");

        var attachmentIds = command.AttachmentIds?.Distinct().ToList() ?? [];
        var linked = new List<Attachment>();
        foreach (var attachmentId in attachmentIds)
        {
            var attachment = await attachments.GetAsync(user.TenantId, attachmentId, ct);
            if (attachment is null || attachment.TicketId != id)
                throw ValidationException.For("attachmentIds", $"Attachment '{attachmentId}' does not belong to this ticket.");
            if (attachment.Status != AttachmentStatuses.Available)
                throw ValidationException.For("attachmentIds", $"Attachment '{attachment.FileName}' is not available ({attachment.Status}).");
            linked.Add(attachment);
        }

        var citedIds = command.CitedKbArticleIds?.Distinct().ToList() ?? [];
        var (cited, published) = await kb.ResolveCitationsAsync(user, citedIds, publishDrafts: isPublic, id, ct);

        var now = clock.UtcNow;
        var comment = await repository.AddTimelineEntryAsync(new TimelineEntry
        {
            Id = TenantDocument.NewId(),
            TenantId = user.TenantId,
            DocType = DocTypes.Comment,
            TicketId = id,
            Author = user.AsPerson(),
            Visibility = command.Visibility,
            Body = command.Body,
            AttachmentIds = attachmentIds,
            CitedKbArticles = cited.Select(a => new CitedKbArticle(a.Id, a.Number, a.Version, a.Title)).ToList(),
            AiAssisted = command.AiAssisted,
            CreatedAt = now,
            CreatedBy = user.UserId,
            UpdatedAt = now,
            UpdatedBy = user.UserId
        }, ct);

        foreach (var attachment in linked.Where(a => a.CommentId is null))
        {
            attachment.CommentId = comment.Id;
            await attachments.ReplaceAsync(attachment, null, ct);
        }

        var updated = await MutateAsync(user, id, null, t =>
        {
            var events = new List<TimelineEntry>();
            foreach (var article in published)
                events.Add(NewEvent(t, user, TicketEventTypes.KbPublished,
                    [new FieldChange("kbArticle", article.Number, KbStatuses.Published)], $"Published {article.Number} while citing it in a reply."));
            var newlyCited = cited.Where(a => !t.KbArticleIds.Contains(a.Id)).ToList();
            if (newlyCited.Count > 0)
            {
                t.KbArticleIds.AddRange(newlyCited.Select(a => a.Id));
                events.Add(NewEvent(t, user, TicketEventTypes.KbCited,
                    newlyCited.Select(a => new FieldChange("kbArticle", null, $"{a.Number} v{a.Version}")).ToList()));
            }
            if (isPublic && user.UserId != t.Requester.Id)
            {
                t.Sla.FirstRespondedAt ??= now;
                if (t.Status == TicketStatuses.New && command.Transition is null)
                    events.AddRange(ApplyTransition(t, user, new TransitionTicketCommand(TicketStatuses.Open)));
            }
            if (command.Transition is not null && command.Transition.Status != t.Status)
                events.AddRange(ApplyTransition(t, user, command.Transition));
            return events;
        }, ct, forceSave: true);

        await notifier.TimelineEntryAddedAsync(updated, comment, ct);
        foreach (var article in published) await notifier.KbChangedAsync(article, ct);
        return new CommentResult(updated, comment, published);
    }

    /// <summary>Records an audit event without changing the ticket (for example a completed attachment upload).</summary>
    public async Task<Ticket> RecordAttachmentAsync(CurrentUser user, Attachment attachment, CancellationToken ct = default) =>
        await MutateAsync(user, attachment.TicketId, null, t =>
        {
            t.AttachmentCount++;
            return [NewEvent(t, user, TicketEventTypes.AttachmentAdded, [new FieldChange("attachment", null, attachment.FileName)])];
        }, ct, forceSave: true);

    private IReadOnlyList<TimelineEntry> ApplyTransition(Ticket ticket, CurrentUser user, TransitionTicketCommand command)
    {
        var from = ticket.Status;
        if (from == command.Status && command.Status != TicketStatuses.Pending) return [];
        if (from != command.Status && !TicketStateMachine.CanTransition(from, command.Status))
        {
            var allowed = TicketStateMachine.AllowedFrom(from);
            throw new InvalidTransitionException(
                $"Cannot move a ticket from {from} to {command.Status}. Allowed: {(allowed.Count > 0 ? string.Join(", ", allowed) : "none")}.");
        }

        var now = clock.UtcNow;
        var changes = new List<FieldChange> { new("status", from, command.Status) };
        if (command.Status == TicketStatuses.Pending)
        {
            changes.Add(new FieldChange("pendingReason", ticket.PendingReason, command.PendingReason));
            ticket.PendingReason = command.PendingReason;
        }
        else
        {
            ticket.PendingReason = null;
        }

        if (command.Status == TicketStatuses.Resolved)
        {
            ticket.Resolution = new Resolution
            {
                Code = command.ResolutionCode!,
                Notes = command.Notes,
                ResolvedAt = now,
                ResolvedBy = user.UserId
            };
            changes.Add(new FieldChange("resolutionCode", null, command.ResolutionCode));
        }
        else if (from == TicketStatuses.Resolved && command.Status == TicketStatuses.Open)
        {
            ticket.Resolution = null;
        }

        ticket.Status = command.Status;
        SlaCalculator.Evaluate(ticket, now);
        return [NewEvent(ticket, user, TicketEventTypes.StatusChanged, changes,
            command.Status == TicketStatuses.Resolved ? null : command.Notes)];
    }

    private async Task<Ticket> MutateAsync(
        CurrentUser user, string id, string? ifMatch, Func<Ticket, IReadOnlyList<TimelineEntry>> mutate,
        CancellationToken ct, bool forceSave = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            var ticket = await repository.GetAsync(user.TenantId, id, ct) ?? throw new NotFoundException("Ticket", id);
            if (ifMatch is not null && ticket.ETag != ifMatch) throw new ConcurrencyConflictException();

            var events = mutate(ticket);
            if (events.Count == 0 && !forceSave) return ticket;

            var now = clock.UtcNow;
            ticket.UpdatedAt = now;
            ticket.UpdatedBy = user.UserId;
            ticket.LastActivityAt = now;
            SlaCalculator.Evaluate(ticket, now);
            Ticket saved;
            try
            {
                saved = await repository.ReplaceAsync(ticket, ifMatch ?? ticket.ETag, ct);
            }
            catch (ConcurrencyConflictException) when (ifMatch is null && attempt < MaxConflictRetries)
            {
                logger.LogDebug("Retrying update of ticket {TicketId} after a concurrent change", id);
                continue;
            }

            foreach (var entry in events) await repository.AddTimelineEntryAsync(entry, ct);
            if (events.Count > 0 || forceSave) await notifier.TicketUpdatedAsync(saved, ct);
            return saved;
        }
    }

    private async Task<Ticket> NewTicketAsync(CurrentUser user, string id, CreateTicketCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var type = command.Type ?? TicketTypes.Incident;
        var number = await repository.NextNumberAsync(user.TenantId, CounterName, ct);
        var ticket = new Ticket
        {
            Id = id,
            TenantId = user.TenantId,
            Number = Numbering.Ticket(options.Value.PrefixByType ? type : TicketTypes.Incident, number),
            Subject = command.Subject.Trim(),
            Description = command.Description,
            Type = type,
            Status = TicketStatuses.New,
            Impact = command.Impact ?? 3,
            Urgency = command.Urgency ?? 3,
            Category = command.Category,
            Subcategory = command.Subcategory,
            Tags = command.Tags?.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList() ?? [],
            Requester = command.Requester ?? user.AsPerson(),
            AssignmentGroup = command.AssignmentGroup ?? options.Value.DefaultAssignmentGroup,
            Assignee = command.Assignee,
            Channel = command.Channel ?? Channels.Agent,
            Source = command.Source,
            CreatedAt = now,
            CreatedBy = user.UserId,
            UpdatedAt = now,
            UpdatedBy = user.UserId,
            LastActivityAt = now
        };
        ticket.Priority = PriorityMatrix.Compute(ticket.Impact, ticket.Urgency);
        _sla.Apply(ticket, now);
        return ticket;
    }

    private async Task<Ticket> PersistNewAsync(CurrentUser user, Ticket ticket, CancellationToken ct)
    {
        var created = await repository.CreateAsync(ticket, ct);
        await repository.AddTimelineEntryAsync(NewEvent(created, user, TicketEventTypes.Created,
            [new FieldChange("status", null, created.Status), new FieldChange("priority", null, created.Priority)],
            $"Created via {created.Channel}."), ct);
        await notifier.TicketCreatedAsync(created, ct);
        logger.LogInformation("Created ticket {TicketNumber} ({TicketId}) via {Channel}", created.Number, created.Id, created.Channel);
        return created;
    }

    private TimelineEntry NewEvent(Ticket ticket, CurrentUser user, string eventType, List<FieldChange> changes, string? note = null)
    {
        var now = clock.UtcNow;
        return new TimelineEntry
        {
            Id = TenantDocument.NewId(),
            TenantId = ticket.TenantId,
            DocType = DocTypes.Event,
            TicketId = ticket.Id,
            Author = user.AsPerson(),
            EventType = eventType,
            Changes = changes,
            Note = note,
            CreatedAt = now,
            CreatedBy = user.UserId,
            UpdatedAt = now,
            UpdatedBy = user.UserId
        };
    }

    private static void ValidateCreate(CreateTicketCommand command) =>
        new Validator()
            .Required("subject", command.Subject, MaxSubject)
            .Required("description", command.Description, MaxDescription)
            .OneOf("type", command.Type, TicketTypes.All, optional: true)
            .Range("impact", command.Impact, 1, 3)
            .Range("urgency", command.Urgency, 1, 3)
            .OneOf("category", command.Category, TicketCategories.All, optional: true)
            .MaxLength("subcategory", command.Subcategory, 100)
            .OneOf("channel", command.Channel, Channels.All, optional: true)
            .MaxLength("assignmentGroup", command.AssignmentGroup, 100)
            .When(command.Requester is { } r && (string.IsNullOrWhiteSpace(r.Id) || r.Id.Contains(':')), "requester.id", "is invalid.")
            .When(command.Tags is { Count: > 20 }, "tags", "must contain at most 20 tags.")
            .ThrowIfInvalid();

    private static void ValidateTransition(TransitionTicketCommand command) =>
        new Validator()
            .OneOf("status", command.Status, TicketStatuses.All)
            .When(command.Status == TicketStatuses.Pending && !PendingReasons.All.Contains(command.PendingReason ?? ""),
                "pendingReason", $"is required when status is pending and must be one of: {string.Join(", ", PendingReasons.All)}.")
            .When(command.Status == TicketStatuses.Resolved && !ResolutionCodes.All.Contains(command.ResolutionCode ?? ""),
                "resolutionCode", $"is required when status is resolved and must be one of: {string.Join(", ", ResolutionCodes.All)}.")
            .MaxLength("notes", command.Notes, 4000)
            .ThrowIfInvalid();

    internal static string DeterministicId(string tenantId, string eventId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"intake\n{tenantId}\n{eventId}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "\u2026";
}
