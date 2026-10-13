import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { api } from '../lib/api';
import { LEVEL_LABELS, humanize } from '../lib/format';
import { keys, useVocabulary } from '../lib/queries';
import type { CreateTicketInput, TicketType } from '../lib/types';
import { ErrorAlert, Field } from '../components/ui';

export function NewTicketPage() {
  const vocab = useVocabulary();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [form, setForm] = useState({
    subject: '',
    description: '',
    type: 'incident' as TicketType,
    impact: 3,
    urgency: 3,
    category: '',
    requesterName: '',
    requesterEmail: '',
    assignmentGroup: '',
    tags: '',
  });
  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm((f) => ({ ...f, [key]: value }));

  const create = useMutation({
    mutationFn: (input: CreateTicketInput) => api.createTicket(input),
    onSuccess: (ticket) => {
      queryClient.setQueryData(keys.ticket(ticket.id), ticket);
      void queryClient.invalidateQueries({ queryKey: keys.ticketLists });
      navigate(`/tickets/${ticket.id}`);
    },
  });

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    const requesterName = form.requesterName.trim();
    const requesterEmail = form.requesterEmail.trim();
    create.mutate({
      subject: form.subject.trim(),
      description: form.description.trim(),
      type: form.type,
      impact: form.impact,
      urgency: form.urgency,
      category: form.category || undefined,
      assignmentGroup: form.assignmentGroup.trim() || undefined,
      tags: form.tags.split(',').map((t) => t.trim()).filter(Boolean),
      channel: 'agent',
      requester:
        requesterName || requesterEmail
          ? { id: requesterEmail || requesterName, displayName: requesterName || requesterEmail, email: requesterEmail || undefined }
          : undefined,
    });
  };

  return (
    <form onSubmit={onSubmit} className="card mx-auto flex max-w-3xl flex-col gap-4">
      <h1 className="text-xl font-semibold text-slate-900">New ticket</h1>
      <p className="text-sm text-slate-600">
        Log a ticket on someone's behalf. Leave the requester blank to log it as yourself.
      </p>
      <Field label="Subject" htmlFor="nt-subject">
        <input id="nt-subject" className="input" required maxLength={200} value={form.subject} onChange={(e) => set('subject', e.target.value)} />
      </Field>
      <Field label="Description" htmlFor="nt-description" hint="Markdown is supported.">
        <textarea
          id="nt-description"
          className="input min-h-32"
          required
          value={form.description}
          onChange={(e) => set('description', e.target.value)}
        />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Type" htmlFor="nt-type">
          <select id="nt-type" className="input" value={form.type} onChange={(e) => set('type', e.target.value as TicketType)}>
            {(vocab.data?.ticketTypes ?? ['incident']).map((t) => (
              <option key={t} value={t}>
                {humanize(t)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Category" htmlFor="nt-category">
          <select id="nt-category" className="input" value={form.category} onChange={(e) => set('category', e.target.value)}>
            <option value="">Not set</option>
            {(vocab.data?.categories ?? []).map((c) => (
              <option key={c} value={c}>
                {humanize(c)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Impact" htmlFor="nt-impact" hint="How many people or how much of the business is affected.">
          <select id="nt-impact" className="input" value={form.impact} onChange={(e) => set('impact', Number(e.target.value))}>
            {(vocab.data?.impactLevels ?? [1, 2, 3]).map((l) => (
              <option key={l} value={l}>
                {l} – {LEVEL_LABELS[l]}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Urgency" htmlFor="nt-urgency" hint="How quickly it needs fixing. Priority is derived from both.">
          <select id="nt-urgency" className="input" value={form.urgency} onChange={(e) => set('urgency', Number(e.target.value))}>
            {(vocab.data?.urgencyLevels ?? [1, 2, 3]).map((l) => (
              <option key={l} value={l}>
                {l} – {LEVEL_LABELS[l]}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Requester name" htmlFor="nt-req-name">
          <input id="nt-req-name" className="input" value={form.requesterName} onChange={(e) => set('requesterName', e.target.value)} />
        </Field>
        <Field label="Requester email" htmlFor="nt-req-email">
          <input
            id="nt-req-email"
            type="email"
            className="input"
            value={form.requesterEmail}
            onChange={(e) => set('requesterEmail', e.target.value)}
          />
        </Field>
        <Field label="Assignment group" htmlFor="nt-group">
          <input id="nt-group" className="input" value={form.assignmentGroup} onChange={(e) => set('assignmentGroup', e.target.value)} />
        </Field>
        <Field label="Tags" htmlFor="nt-tags" hint="Comma-separated.">
          <input id="nt-tags" className="input" value={form.tags} onChange={(e) => set('tags', e.target.value)} />
        </Field>
      </div>
      <ErrorAlert error={create.error} />
      <div className="flex justify-end gap-2">
        <button type="button" className="btn-secondary" onClick={() => navigate('/tickets')}>
          Cancel
        </button>
        <button type="submit" className="btn-primary" disabled={create.isPending}>
          {create.isPending ? 'Creating…' : 'Create ticket'}
        </button>
      </div>
    </form>
  );
}
