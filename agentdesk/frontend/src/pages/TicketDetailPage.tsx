import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { AiPanel } from '../components/AiPanel';
import { Composer, emptyComposer, type ComposerState } from '../components/Composer';
import { TicketProperties } from '../components/TicketProperties';
import { AttachmentLink, Timeline } from '../components/Timeline';
import { ErrorAlert, MarkdownView, PriorityBadge, SlaBadge, Spinner, StatusBadge, Badge } from '../components/ui';
import { ApiError } from '../lib/api';
import { formatBytes, formatDateTime, humanize } from '../lib/format';
import { useAttachments, useTicket, useTimeline } from '../lib/queries';
import type { KbArticle } from '../lib/types';

export function TicketDetailPage() {
  const { id = '' } = useParams();
  const ticket = useTicket(id);
  const timeline = useTimeline(id);
  const attachments = useAttachments(id);
  const [composer, setComposer] = useState<ComposerState>(emptyComposer);
  const [published, setPublished] = useState<KbArticle[]>([]);

  if (ticket.isPending) return <Spinner label="Loading ticket…" />;
  if (ticket.isError) {
    if (ticket.error instanceof ApiError && ticket.error.status === 404)
      return (
        <div className="card">
          <h1 className="text-lg font-semibold">Ticket not found</h1>
          <p className="mt-1 text-sm text-slate-700">
            It may have been removed, or the link is wrong. <Link to="/tickets" className="text-blue-700 underline">Back to tickets</Link>
          </p>
        </div>
      );
    return <ErrorAlert error={ticket.error} onRetry={() => void ticket.refetch()} />;
  }

  const t = ticket.data;
  const files = attachments.data ?? [];

  return (
    <div className="flex flex-col gap-4">
      <nav aria-label="Breadcrumb" className="text-sm">
        <Link to="/tickets" className="text-blue-700 underline">
          Tickets
        </Link>{' '}
        / <span aria-current="page">{t.number}</span>
      </nav>
      <header className="card flex flex-col gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-mono text-sm text-slate-700">{t.number}</span>
          <StatusBadge status={t.status} />
          <PriorityBadge priority={t.priority} />
          <SlaBadge ticket={t} />
          <Badge>{humanize(t.type)}</Badge>
        </div>
        <h1 className="text-xl font-semibold text-slate-900">{t.subject}</h1>
        <p className="text-sm text-slate-700">
          From <strong>{t.requester.displayName}</strong>
          {t.requester.email && <> ({t.requester.email})</>} via {humanize(t.channel)} · opened {formatDateTime(t.createdAt)}
        </p>
        <div className="border-t border-slate-200 pt-2">
          <h2 className="sr-only">Description</h2>
          <MarkdownView>{t.description}</MarkdownView>
        </div>
        {t.conversationSummary && (
          <section className="border-t border-slate-200 pt-2" aria-labelledby="conversation-summary-heading">
            <h2 id="conversation-summary-heading" className="text-sm font-semibold text-slate-900">
              Conversation so far
            </h2>
            <p className="mt-1 whitespace-pre-line text-sm text-slate-700">{t.conversationSummary}</p>
          </section>
        )}
      </header>

      {published.length > 0 && (
        <div role="status" className="rounded-md border border-green-300 bg-green-50 p-3 text-sm text-green-900">
          Published with this reply:{' '}
          {published.map((a, i) => (
            <span key={a.id}>
              {i > 0 && ', '}
              <Link to={`/kb/${a.id}`} className="underline">
                {a.number} — {a.title}
              </Link>
            </span>
          ))}
          .{' '}
          <button type="button" className="underline" onClick={() => setPublished([])}>
            Dismiss
          </button>
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="flex min-w-0 flex-col gap-4">
          <section aria-labelledby="activity-h" className="flex flex-col gap-2">
            <h2 id="activity-h" className="text-lg font-semibold">
              Activity
            </h2>
            {timeline.isPending ? (
              <Spinner label="Loading activity…" />
            ) : timeline.isError ? (
              <ErrorAlert error={timeline.error} onRetry={() => void timeline.refetch()} />
            ) : (
              <Timeline entries={timeline.data} attachments={files} />
            )}
          </section>
          <Composer ticket={t} state={composer} onChange={setComposer} onSent={setPublished} />
        </div>
        <aside className="flex flex-col gap-4" aria-label="Ticket tools">
          <AiPanel ticket={t} composer={composer} onComposerChange={setComposer} />
          <TicketProperties ticket={t} />
          <section aria-labelledby="files-h" className="card">
            <h2 id="files-h" className="mb-2 font-semibold">
              Attachments
            </h2>
            {attachments.isError ? (
              <ErrorAlert error={attachments.error} onRetry={() => void attachments.refetch()} />
            ) : files.length === 0 ? (
              <p className="text-sm text-slate-600">No attachments.</p>
            ) : (
              <ul className="flex flex-col gap-1 text-sm">
                {files.map((a) => (
                  <li key={a.id} className="flex justify-between gap-2">
                    <AttachmentLink attachment={a} />
                    <span className="text-slate-600">{formatBytes(a.size)}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </aside>
      </div>
    </div>
  );
}
