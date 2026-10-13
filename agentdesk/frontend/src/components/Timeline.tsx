import { Link } from 'react-router';
import { api } from '../lib/api';
import { formatBytes, formatDateTime, humanize, relativeTime, statusLabel } from '../lib/format';
import type { Attachment, FieldChange, TimelineEntry } from '../lib/types';
import { AiLabel, Badge, MarkdownView } from './ui';

export function Timeline({ entries, attachments }: { entries: TimelineEntry[]; attachments: Attachment[] }) {
  const byId = new Map(attachments.map((a) => [a.id, a]));
  if (entries.length === 0) return <p className="text-sm text-slate-600">No activity yet.</p>;
  return (
    <ol aria-label="Ticket timeline" className="flex flex-col gap-3">
      {entries.map((e) =>
        e.docType === 'comment' ? (
          <CommentEntry key={e.id} entry={e} attachments={(e.attachmentIds ?? []).map((id) => byId.get(id)).filter((a) => !!a)} />
        ) : (
          <EventEntry key={e.id} entry={e} />
        ),
      )}
    </ol>
  );
}

function CommentEntry({ entry, attachments }: { entry: TimelineEntry; attachments: Attachment[] }) {
  const internal = entry.visibility === 'internal';
  return (
    <li
      className={`rounded-lg border p-3 shadow-sm ${internal ? 'border-amber-300 bg-amber-50' : 'border-slate-200 bg-white'}`}
      aria-label={`${internal ? 'Internal note' : 'Public reply'} from ${entry.author.displayName}`}
    >
      <div className="mb-2 flex flex-wrap items-center gap-2 text-sm">
        <span className="font-medium text-slate-900">{entry.author.displayName}</span>
        <Badge tone={internal ? 'amber' : 'blue'}>{internal ? 'Internal note' : 'Public reply'}</Badge>
        <AiLabel ai={entry.aiAssisted} />
        <time dateTime={entry.createdAt} title={formatDateTime(entry.createdAt)} className="ml-auto text-xs text-slate-600">
          {relativeTime(entry.createdAt)}
        </time>
      </div>
      {entry.body && <MarkdownView>{entry.body}</MarkdownView>}
      {(entry.citedKbArticles?.length ?? 0) > 0 && (
        <div className="mt-2 flex flex-wrap items-center gap-2 text-xs">
          <span className="font-medium text-slate-700">Cited:</span>
          {entry.citedKbArticles!.map((kb) => (
            <Link key={kb.id} to={`/kb/${kb.id}`} className="rounded bg-green-50 px-2 py-0.5 text-green-900 ring-1 ring-green-300 hover:underline">
              {kb.number} v{kb.version} — {kb.title}
            </Link>
          ))}
        </div>
      )}
      {attachments.length > 0 && (
        <ul className="mt-2 flex flex-wrap gap-2 text-xs" aria-label="Attachments">
          {attachments.map((a) => (
            <li key={a.id}>
              <AttachmentLink attachment={a} />
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}

function EventEntry({ entry }: { entry: TimelineEntry }) {
  return (
    <li className="flex flex-wrap items-baseline gap-x-2 px-3 text-xs text-slate-700">
      <span className="font-medium">{entry.author.displayName}</span>
      <span>{describeEvent(entry)}</span>
      {entry.note && <span className="italic">“{entry.note}”</span>}
      <time dateTime={entry.createdAt} title={formatDateTime(entry.createdAt)} className="ml-auto text-slate-600">
        {relativeTime(entry.createdAt)}
      </time>
    </li>
  );
}

function describeChange(c: FieldChange): string {
  const fmt = (v?: string) => (v === undefined || v === '' ? 'none' : c.field === 'status' ? statusLabel(v) : humanize(v));
  return `${humanize(c.field).toLowerCase()} ${fmt(c.from)} → ${fmt(c.to)}`;
}

export function describeEvent(entry: TimelineEntry): string {
  const type = humanize(entry.eventType ?? 'updated').toLowerCase();
  const changes = entry.changes?.map(describeChange).join('; ');
  return changes ? `${type}: ${changes}` : type;
}

export function AttachmentLink({ attachment }: { attachment: Attachment }) {
  const available = attachment.status === 'available';
  const open = async () => {
    const { downloadUrl } = await api.downloadAttachment(attachment.ticketId, attachment.id);
    window.open(downloadUrl, '_blank', 'noopener');
  };
  const label = `${attachment.fileName} (${formatBytes(attachment.size)})`;
  if (!available)
    return (
      <span className="rounded bg-slate-100 px-2 py-0.5 text-slate-700 ring-1 ring-slate-300" title={attachment.scanResult}>
        {label} — {humanize(attachment.status)}
      </span>
    );
  return (
    <button
      type="button"
      onClick={() => void open()}
      className="rounded bg-white px-2 py-0.5 text-blue-800 ring-1 ring-slate-300 hover:bg-slate-50 hover:underline"
    >
      {label}
    </button>
  );
}
