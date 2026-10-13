import type { ReactNode } from 'react';
import Markdown from 'react-markdown';
import { ApiError } from '../lib/api';
import { PRIORITY_LABELS, humanize, slaState, statusLabel } from '../lib/format';
import type { AiAssisted, EmbeddingInfo, KbStatus, Priority, Ticket, TicketStatus } from '../lib/types';

const TONES = {
  slate: 'bg-slate-100 text-slate-800 ring-slate-300',
  blue: 'bg-blue-100 text-blue-900 ring-blue-300',
  green: 'bg-green-100 text-green-900 ring-green-300',
  amber: 'bg-amber-100 text-amber-900 ring-amber-300',
  red: 'bg-red-100 text-red-900 ring-red-300',
  violet: 'bg-violet-100 text-violet-900 ring-violet-300',
} as const;

export type Tone = keyof typeof TONES;

export function Badge({ tone = 'slate', children, title }: { tone?: Tone; children: ReactNode; title?: string }) {
  return (
    <span
      title={title}
      className={`inline-flex items-center whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${TONES[tone]}`}
    >
      {children}
    </span>
  );
}

const STATUS_TONES: Record<TicketStatus, Tone> = {
  new: 'blue',
  open: 'violet',
  pending: 'amber',
  on_hold: 'amber',
  resolved: 'green',
  closed: 'slate',
  cancelled: 'slate',
};

export const StatusBadge = ({ status }: { status: TicketStatus }) => (
  <Badge tone={STATUS_TONES[status] ?? 'slate'}>{statusLabel(status)}</Badge>
);

const PRIORITY_TONES: Record<Priority, Tone> = { P1: 'red', P2: 'amber', P3: 'blue', P4: 'slate' };

export const PriorityBadge = ({ priority }: { priority: Priority }) => (
  <Badge tone={PRIORITY_TONES[priority] ?? 'slate'} title={PRIORITY_LABELS[priority]}>
    {priority}
  </Badge>
);

export function SlaBadge({ ticket }: { ticket: Pick<Ticket, 'status' | 'sla'> }) {
  const s = slaState(ticket);
  if (s.tone === 'none') return null;
  const tone: Tone = s.tone === 'breached' ? 'red' : s.tone === 'warn' ? 'amber' : s.tone === 'met' ? 'green' : 'slate';
  return <Badge tone={tone}>{s.label}</Badge>;
}

const KB_TONES: Record<KbStatus, Tone> = { draft: 'amber', published: 'green', archived: 'slate' };

export const KbStatusBadge = ({ status }: { status: KbStatus }) => (
  <Badge tone={KB_TONES[status] ?? 'slate'}>{humanize(status)}</Badge>
);

export function EmbeddingBadge({ embedding }: { embedding?: EmbeddingInfo }) {
  if (!embedding) return null;
  const tone: Tone =
    embedding.status === 'ready' ? 'green' : embedding.status === 'failed' ? 'red' : embedding.status === 'pending' ? 'amber' : 'slate';
  const detail = embedding.model ? `${embedding.provider ?? ''} ${embedding.model} (${embedding.dimensions ?? '?'}d)`.trim() : undefined;
  return (
    <Badge tone={tone} title={embedding.error ?? detail}>
      Embedding: {humanize(embedding.status)}
    </Badge>
  );
}

export function AiLabel({ ai }: { ai?: AiAssisted }) {
  if (!ai) return null;
  return (
    <Badge tone="violet" title={`${humanize(ai.feature)} · ${ai.provider} ${ai.model} · prompt ${ai.promptVersion}`}>
      AI-assisted
    </Badge>
  );
}

export function MarkdownView({ children }: { children: string }) {
  return (
    <div className="prose prose-sm prose-slate max-w-none break-words">
      <Markdown>{children}</Markdown>
    </div>
  );
}

export function ErrorAlert({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  if (!error) return null;
  const message = error instanceof Error ? error.message : String(error);
  const correlationId = error instanceof ApiError ? error.correlationId : undefined;
  return (
    <div role="alert" className="rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-900">
      <p>{message}</p>
      {correlationId && <p className="mt-1 text-xs text-red-800">Reference: {correlationId}</p>}
      {onRetry && (
        <button type="button" onClick={onRetry} className="btn-secondary mt-2">
          {error instanceof ApiError && error.isConflict ? 'Reload' : 'Try again'}
        </button>
      )}
    </div>
  );
}

export function Spinner({ label = 'Loading…' }: { label?: string }) {
  return (
    <p role="status" className="flex items-center gap-2 text-sm text-slate-600">
      <span aria-hidden="true" className="h-4 w-4 animate-spin rounded-full border-2 border-slate-300 border-t-slate-700" />
      {label}
    </p>
  );
}

export function Field({ label, htmlFor, children, hint }: { label: string; htmlFor: string; children: ReactNode; hint?: string }) {
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={htmlFor} className="text-sm font-medium text-slate-700">
        {label}
      </label>
      {children}
      {hint && <p className="text-xs text-slate-600">{hint}</p>}
    </div>
  );
}
