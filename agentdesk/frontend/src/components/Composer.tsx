import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useRef, useState, type FormEvent } from 'react';
import { api, uploadAttachment } from '../lib/api';
import { humanize, statusLabel } from '../lib/format';
import { keys, useVocabulary } from '../lib/queries';
import type { AiAssisted, KbArticle, Ticket, TicketStatus, TransitionInput, Visibility } from '../lib/types';
import { AiLabel, ErrorAlert, KbStatusBadge } from './ui';

export interface CitedRef {
  id: string;
  number: string;
  title: string;
}

export interface ComposerState {
  body: string;
  visibility: Visibility;
  aiAssisted?: AiAssisted;
  cited: CitedRef[];
}

export const emptyComposer = (): ComposerState => ({ body: '', visibility: 'public', cited: [] });

export const citationText = (kb: CitedRef) => `${kb.number} — ${kb.title}`;

export function Composer({
  ticket,
  state,
  onChange,
  onSent,
}: {
  ticket: Ticket;
  state: ComposerState;
  onChange: (next: ComposerState) => void;
  onSent?: (published: KbArticle[]) => void;
}) {
  const queryClient = useQueryClient();
  const vocab = useVocabulary();
  const fileInput = useRef<HTMLInputElement>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [status, setStatus] = useState<TicketStatus | ''>('');
  const [pendingReason, setPendingReason] = useState('');
  const [resolutionCode, setResolutionCode] = useState('');
  const nextStatuses = vocab.data?.transitions[ticket.status] ?? [];

  const send = useMutation({
    mutationFn: async () => {
      const attachmentIds: string[] = [];
      for (const file of files) attachmentIds.push((await uploadAttachment(ticket.id, file)).id);
      const transition: TransitionInput | undefined = status
        ? {
            status,
            pendingReason: status === 'pending' ? pendingReason : undefined,
            resolutionCode: status === 'resolved' ? resolutionCode : undefined,
            notes: status === 'resolved' ? state.body.trim() : undefined,
          }
        : undefined;
      return api.addComment(ticket.id, {
        visibility: state.visibility,
        body: state.body.trim(),
        attachmentIds: attachmentIds.length ? attachmentIds : undefined,
        citedKbArticleIds: state.cited.length ? state.cited.map((c) => c.id) : undefined,
        aiAssisted: state.aiAssisted,
        transition,
      });
    },
    onSuccess: (result) => {
      queryClient.setQueryData(keys.ticket(ticket.id), result.ticket);
      void queryClient.invalidateQueries({ queryKey: keys.timeline(ticket.id) });
      void queryClient.invalidateQueries({ queryKey: keys.attachments(ticket.id) });
      void queryClient.invalidateQueries({ queryKey: keys.ticketLists });
      if (result.publishedArticles?.length) void queryClient.invalidateQueries({ queryKey: ['kb'] });
      onChange(emptyComposer());
      setFiles([]);
      setStatus('');
      setPendingReason('');
      setResolutionCode('');
      if (fileInput.current) fileInput.current.value = '';
      onSent?.(result.publishedArticles ?? []);
    },
  });

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (state.body.trim()) send.mutate();
  };

  const cite = (kb: CitedRef) => {
    if (state.cited.some((c) => c.id === kb.id)) return;
    const sep = state.body && !state.body.endsWith('\n') ? '\n\n' : '';
    onChange({ ...state, cited: [...state.cited, kb], body: `${state.body}${sep}See ${citationText(kb)}.` });
  };

  const internal = state.visibility === 'internal';
  const missingTransitionField = (status === 'pending' && !pendingReason) || (status === 'resolved' && !resolutionCode);

  return (
    <form onSubmit={onSubmit} className={`card flex flex-col gap-3 ${internal ? 'border-amber-300 bg-amber-50' : ''}`} aria-label="Reply">
      <fieldset className="flex flex-wrap items-center gap-4">
        <legend className="sr-only">Visibility</legend>
        {(['public', 'internal'] as const).map((v) => (
          <label key={v} className="flex items-center gap-1 text-sm text-slate-800">
            <input
              type="radio"
              name="composer-visibility"
              value={v}
              checked={state.visibility === v}
              onChange={() => onChange({ ...state, visibility: v })}
            />
            {v === 'public' ? 'Public reply (requester sees it)' : 'Internal note'}
          </label>
        ))}
        {state.aiAssisted && (
          <span className="ml-auto flex items-center gap-2">
            <AiLabel ai={state.aiAssisted} />
            <button type="button" className="text-xs text-slate-700 underline" onClick={() => onChange({ ...state, aiAssisted: undefined })}>
              Clear AI label
            </button>
          </span>
        )}
      </fieldset>

      <label htmlFor="composer-body" className="sr-only">
        {internal ? 'Internal note' : 'Reply to requester'}
      </label>
      <textarea
        id="composer-body"
        className="input min-h-36"
        placeholder={internal ? 'Add an internal note…' : 'Write a reply to the requester…'}
        value={state.body}
        onChange={(e) => onChange({ ...state, body: e.target.value })}
      />

      {state.cited.length > 0 && (
        <ul aria-label="Cited articles" className="flex flex-wrap gap-2 text-xs">
          {state.cited.map((c) => (
            <li key={c.id} className="flex items-center gap-1 rounded bg-green-50 px-2 py-0.5 text-green-900 ring-1 ring-green-300">
              {citationText(c)}
              <button
                type="button"
                aria-label={`Remove citation ${c.number}`}
                className="ml-1 font-bold"
                onClick={() => onChange({ ...state, cited: state.cited.filter((x) => x.id !== c.id) })}
              >
                ×
              </button>
            </li>
          ))}
        </ul>
      )}

      <KbCiteSearch onPick={cite} />

      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1">
          <label htmlFor="composer-files" className="text-sm font-medium text-slate-700">
            Attach files
          </label>
          <input
            id="composer-files"
            ref={fileInput}
            type="file"
            multiple
            className="text-sm"
            onChange={(e) => setFiles(Array.from(e.target.files ?? []))}
          />
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="composer-status" className="text-sm font-medium text-slate-700">
            And set status
          </label>
          <select id="composer-status" className="input" value={status} onChange={(e) => setStatus(e.target.value as TicketStatus | '')}>
            <option value="">Leave as {statusLabel(ticket.status)}</option>
            {nextStatuses.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
              </option>
            ))}
          </select>
        </div>
        {status === 'pending' && (
          <ReasonSelect id="composer-pending" label="Pending reason" value={pendingReason} onChange={setPendingReason} options={vocab.data?.pendingReasons ?? []} />
        )}
        {status === 'resolved' && (
          <ReasonSelect
            id="composer-resolution"
            label="Resolution code"
            value={resolutionCode}
            onChange={setResolutionCode}
            options={vocab.data?.resolutionCodes ?? []}
          />
        )}
        <button type="submit" className="btn-primary ml-auto" disabled={!state.body.trim() || send.isPending || missingTransitionField}>
          {send.isPending ? 'Sending…' : internal ? 'Add note' : 'Send reply'}
        </button>
      </div>
      <ErrorAlert error={send.error} />
    </form>
  );
}

export function ReasonSelect({
  id,
  label,
  value,
  onChange,
  options,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: string[];
}) {
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="text-sm font-medium text-slate-700">
        {label}
      </label>
      <select id={id} className="input" required value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Choose…</option>
        {options.map((o) => (
          <option key={o} value={o}>
            {humanize(o)}
          </option>
        ))}
      </select>
    </div>
  );
}

function KbCiteSearch({ onPick }: { onPick: (kb: CitedRef) => void }) {
  const id = useId();
  const [text, setText] = useState('');
  const [q, setQ] = useState('');
  const results = useQuery({
    queryKey: keys.kbList({ q, pageSize: 8 }),
    queryFn: ({ signal }) => api.listKb({ q, pageSize: 8 }, signal),
    enabled: q.length > 0,
  });
  const items = (results.data?.items ?? []).filter((a) => a.status !== 'archived');

  return (
    <details className="rounded-md border border-slate-200 bg-white p-2 text-sm">
      <summary className="cursor-pointer font-medium text-slate-800">Cite a knowledge base article</summary>
      <div className="mt-2 flex items-end gap-2">
        <div className="flex flex-1 flex-col gap-1">
          <label htmlFor={id} className="text-xs text-slate-700">
            Search articles
          </label>
          <input
            id={id}
            className="input"
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                setQ(text.trim());
              }
            }}
          />
        </div>
        <button type="button" className="btn-secondary" onClick={() => setQ(text.trim())}>
          Search
        </button>
      </div>
      {results.isFetching && <p className="mt-2 text-xs text-slate-600">Searching…</p>}
      <ErrorAlert error={results.error} />
      {q && results.isSuccess && items.length === 0 && <p className="mt-2 text-xs text-slate-600">No articles found.</p>}
      {items.length > 0 && (
        <ul className="mt-2 flex flex-col gap-1">
          {items.map((a) => (
            <li key={a.id} className="flex items-center gap-2">
              <span className="font-mono text-xs text-slate-700">{a.number}</span>
              <span className="flex-1 text-slate-900">{a.title}</span>
              <KbStatusBadge status={a.status} />
              <button type="button" className="btn-secondary text-xs" onClick={() => onPick({ id: a.id, number: a.number, title: a.title })}>
                Cite {a.number}
              </button>
            </li>
          ))}
        </ul>
      )}
      <p className="mt-2 text-xs text-slate-600">Citing a draft article in a public reply publishes it.</p>
    </details>
  );
}
