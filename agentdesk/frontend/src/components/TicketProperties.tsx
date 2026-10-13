import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api } from '../lib/api';
import { LEVEL_LABELS, formatDateTime, humanize, statusLabel } from '../lib/format';
import { keys, useMe, useVocabulary } from '../lib/queries';
import type { Ticket, TicketStatus, TicketType, UpdateTicketInput } from '../lib/types';
import { ReasonSelect } from './Composer';
import { ErrorAlert, Field } from './ui';

function useTicketMutation<T>(ticket: Ticket, fn: (input: T) => Promise<Ticket>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: (updated) => {
      queryClient.setQueryData(keys.ticket(ticket.id), updated);
      void queryClient.invalidateQueries({ queryKey: keys.timeline(ticket.id) });
      void queryClient.invalidateQueries({ queryKey: keys.ticketLists });
    },
  });
}

/** Retry handler for a failed mutation: on a 412 the ticket is stale, so refetch it first. */
function useReload(ticket: Ticket, reset: () => void) {
  const queryClient = useQueryClient();
  return () => {
    reset();
    void queryClient.invalidateQueries({ queryKey: keys.ticket(ticket.id) });
  };
}

export function TicketProperties({ ticket }: { ticket: Ticket }) {
  return (
    <div className="flex flex-col gap-4">
      <StatusControl ticket={ticket} />
      <AssignmentControl ticket={ticket} />
      <FieldsControl ticket={ticket} />
      <SlaDetails ticket={ticket} />
    </div>
  );
}

function StatusControl({ ticket }: { ticket: Ticket }) {
  const vocab = useVocabulary();
  const [status, setStatus] = useState<TicketStatus | ''>('');
  const [pendingReason, setPendingReason] = useState('');
  const [resolutionCode, setResolutionCode] = useState('');
  const [notes, setNotes] = useState('');
  const next = vocab.data?.transitions[ticket.status] ?? [];
  const transition = useTicketMutation(ticket, () =>
    api.transitionTicket(
      ticket.id,
      {
        status: status as TicketStatus,
        pendingReason: status === 'pending' ? pendingReason : undefined,
        resolutionCode: status === 'resolved' ? resolutionCode : undefined,
        notes: notes.trim() || undefined,
      },
      ticket._etag,
    ),
  );
  const reload = useReload(ticket, transition.reset);

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    transition.mutate(undefined, {
      onSuccess: () => {
        setStatus('');
        setPendingReason('');
        setResolutionCode('');
        setNotes('');
      },
    });
  };

  if (next.length === 0)
    return (
      <section aria-labelledby="status-h" className="card">
        <h2 id="status-h" className="mb-1 text-sm font-semibold text-slate-900">
          Status
        </h2>
        <p className="text-sm text-slate-700">{statusLabel(ticket.status)} — no further changes allowed.</p>
      </section>
    );

  const incomplete = !status || (status === 'pending' && !pendingReason) || (status === 'resolved' && !resolutionCode);

  return (
    <form onSubmit={onSubmit} aria-labelledby="status-h" className="card flex flex-col gap-2">
      <h2 id="status-h" className="text-sm font-semibold text-slate-900">
        Status: {statusLabel(ticket.status)}
        {ticket.pendingReason && <span className="font-normal text-slate-700"> ({humanize(ticket.pendingReason)})</span>}
      </h2>
      <Field label="Change status to" htmlFor="tp-status">
        <select id="tp-status" className="input" value={status} onChange={(e) => setStatus(e.target.value as TicketStatus | '')}>
          <option value="">Choose…</option>
          {next.map((s) => (
            <option key={s} value={s}>
              {statusLabel(s)}
            </option>
          ))}
        </select>
      </Field>
      {status === 'pending' && (
        <ReasonSelect id="tp-pending" label="Pending reason" value={pendingReason} onChange={setPendingReason} options={vocab.data?.pendingReasons ?? []} />
      )}
      {status === 'resolved' && (
        <ReasonSelect
          id="tp-resolution"
          label="Resolution code"
          value={resolutionCode}
          onChange={setResolutionCode}
          options={vocab.data?.resolutionCodes ?? []}
        />
      )}
      {status && (
        <Field label={status === 'resolved' ? 'Resolution notes' : 'Note (optional)'} htmlFor="tp-notes">
          <textarea id="tp-notes" className="input" value={notes} onChange={(e) => setNotes(e.target.value)} />
        </Field>
      )}
      <button type="submit" className="btn-primary" disabled={incomplete || transition.isPending}>
        {transition.isPending ? 'Updating…' : 'Update status'}
      </button>
      <ErrorAlert error={transition.error} onRetry={reload} />
      {ticket.resolution && (
        <p className="text-xs text-slate-700">
          Resolved as {humanize(ticket.resolution.code)} by {ticket.resolution.resolvedBy.displayName} on{' '}
          {formatDateTime(ticket.resolution.resolvedAt)}.
        </p>
      )}
    </form>
  );
}

function AssignmentControl({ ticket }: { ticket: Ticket }) {
  const me = useMe();
  const [group, setGroup] = useState(ticket.assignmentGroup ?? '');
  const assign = useTicketMutation(ticket, (body: Parameters<typeof api.assignTicket>[1]) =>
    api.assignTicket(ticket.id, body, ticket._etag),
  );
  const reload = useReload(ticket, assign.reset);
  const mine = me.data && ticket.assignee?.id === me.data.userId;

  return (
    <section aria-labelledby="assign-h" className="card flex flex-col gap-2">
      <h2 id="assign-h" className="text-sm font-semibold text-slate-900">
        Assignment
      </h2>
      <p className="text-sm text-slate-800">
        Assignee: <span className="font-medium">{ticket.assignee?.displayName ?? 'Unassigned'}</span>
      </p>
      {me.data && !mine && (
        <button
          type="button"
          className="btn-secondary"
          disabled={assign.isPending}
          onClick={() =>
            assign.mutate({
              assignmentGroup: ticket.assignmentGroup,
              assignee: { id: me.data.userId, displayName: me.data.displayName ?? me.data.userId, email: me.data.email },
            })
          }
        >
          Assign to me
        </button>
      )}
      <form
        className="flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          assign.mutate({ assignmentGroup: group.trim() || undefined, assignee: ticket.assignee });
        }}
      >
        <div className="flex flex-1 flex-col gap-1">
          <label htmlFor="tp-group" className="text-sm font-medium text-slate-700">
            Assignment group
          </label>
          <input id="tp-group" className="input" value={group} onChange={(e) => setGroup(e.target.value)} />
        </div>
        <button type="submit" className="btn-secondary" disabled={assign.isPending || group.trim() === (ticket.assignmentGroup ?? '')}>
          Save
        </button>
      </form>
      <ErrorAlert error={assign.error} onRetry={reload} />
    </section>
  );
}

function FieldsControl({ ticket }: { ticket: Ticket }) {
  const vocab = useVocabulary();
  const initial = () => ({
    type: ticket.type,
    impact: ticket.impact,
    urgency: ticket.urgency,
    category: ticket.category ?? '',
    tags: (ticket.tags ?? []).join(', '),
  });
  const [form, setForm] = useState(initial);
  const [baseEtag, setBaseEtag] = useState(ticket._etag);
  if (baseEtag !== ticket._etag) {
    setBaseEtag(ticket._etag);
    setForm(initial());
  }
  const update = useTicketMutation(ticket, (body: UpdateTicketInput) => api.updateTicket(ticket.id, body, ticket._etag));
  const reload = useReload(ticket, update.reset);
  const tags = form.tags.split(',').map((t) => t.trim()).filter(Boolean);
  const dirty =
    form.type !== ticket.type ||
    form.impact !== ticket.impact ||
    form.urgency !== ticket.urgency ||
    form.category !== (ticket.category ?? '') ||
    tags.join(',') !== (ticket.tags ?? []).join(',');

  return (
    <form
      aria-labelledby="fields-h"
      className="card flex flex-col gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        update.mutate({ type: form.type, impact: form.impact, urgency: form.urgency, category: form.category || undefined, tags });
      }}
    >
      <h2 id="fields-h" className="text-sm font-semibold text-slate-900">
        Details
      </h2>
      <Field label="Type" htmlFor="tp-type">
        <select id="tp-type" className="input" value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value as TicketType })}>
          {(vocab.data?.ticketTypes ?? [ticket.type]).map((t) => (
            <option key={t} value={t}>
              {humanize(t)}
            </option>
          ))}
        </select>
      </Field>
      <div className="grid grid-cols-2 gap-2">
        <Field label="Impact" htmlFor="tp-impact">
          <select id="tp-impact" className="input" value={form.impact} onChange={(e) => setForm({ ...form, impact: Number(e.target.value) })}>
            {[1, 2, 3].map((l) => (
              <option key={l} value={l}>
                {l} – {LEVEL_LABELS[l]}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Urgency" htmlFor="tp-urgency">
          <select id="tp-urgency" className="input" value={form.urgency} onChange={(e) => setForm({ ...form, urgency: Number(e.target.value) })}>
            {[1, 2, 3].map((l) => (
              <option key={l} value={l}>
                {l} – {LEVEL_LABELS[l]}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <Field label="Category" htmlFor="tp-category">
        <select id="tp-category" className="input" value={form.category} onChange={(e) => setForm({ ...form, category: e.target.value })}>
          <option value="">Not set</option>
          {(vocab.data?.categories ?? []).map((c) => (
            <option key={c} value={c}>
              {humanize(c)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Tags" htmlFor="tp-tags" hint="Comma-separated.">
        <input id="tp-tags" className="input" value={form.tags} onChange={(e) => setForm({ ...form, tags: e.target.value })} />
      </Field>
      <button type="submit" className="btn-secondary" disabled={!dirty || update.isPending}>
        {update.isPending ? 'Saving…' : 'Save details'}
      </button>
      <ErrorAlert error={update.error} onRetry={reload} />
    </form>
  );
}

function SlaDetails({ ticket }: { ticket: Ticket }) {
  if (!ticket.sla) return null;
  const { sla } = ticket;
  return (
    <section aria-labelledby="sla-h" className="card text-sm">
      <h2 id="sla-h" className="mb-1 font-semibold text-slate-900">
        SLA ({sla.policyId})
      </h2>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-slate-800">
        <dt className="text-slate-600">First response due</dt>
        <dd>
          {formatDateTime(sla.firstResponseDueAt)}
          {sla.firstResponseBreached && <span className="ml-1 font-medium text-red-800">(breached)</span>}
        </dd>
        <dt className="text-slate-600">Responded</dt>
        <dd>{sla.firstRespondedAt ? formatDateTime(sla.firstRespondedAt) : 'Not yet'}</dd>
        <dt className="text-slate-600">Resolution due</dt>
        <dd>
          {formatDateTime(sla.resolutionDueAt)}
          {sla.resolutionBreached && <span className="ml-1 font-medium text-red-800">(breached)</span>}
        </dd>
      </dl>
    </section>
  );
}
