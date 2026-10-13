import type { Priority, Sla, Ticket, TicketStatus } from './types';

/** "email_collaboration" → "Email collaboration". Vocabulary values are snake_case. */
export function humanize(value: string | undefined | null): string {
  if (!value) return '';
  const spaced = value.replace(/_/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

const STATUS_LABELS: Record<TicketStatus, string> = {
  new: 'New',
  open: 'Open',
  pending: 'Pending',
  on_hold: 'On hold',
  resolved: 'Resolved',
  closed: 'Closed',
  cancelled: 'Cancelled',
};

export const statusLabel = (s: TicketStatus | string) => STATUS_LABELS[s as TicketStatus] ?? humanize(s);

export const PRIORITY_LABELS: Record<Priority, string> = {
  P1: 'P1 – Critical',
  P2: 'P2 – High',
  P3: 'P3 – Medium',
  P4: 'P4 – Low',
};

export const LEVEL_LABELS: Record<number, string> = { 1: 'High', 2: 'Medium', 3: 'Low' };

export function formatDateTime(iso: string | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** Rough, human relative time ("5 min ago", "in 2 h"). */
export function relativeTime(iso: string | undefined, now: number = Date.now()): string {
  if (!iso) return '';
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return iso;
  const diff = t - now;
  const abs = Math.abs(diff);
  const minute = 60_000;
  const hour = 60 * minute;
  const day = 24 * hour;
  let text: string;
  if (abs < minute) return diff >= 0 ? 'in under a minute' : 'just now';
  if (abs < hour) text = `${Math.round(abs / minute)} min`;
  else if (abs < day) text = `${Math.round(abs / hour)} h`;
  else text = `${Math.round(abs / day)} d`;
  return diff >= 0 ? `in ${text}` : `${text} ago`;
}

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

export type SlaState = { tone: 'ok' | 'warn' | 'breached' | 'met' | 'none'; label: string };

const ACTIVE: TicketStatus[] = ['new', 'open', 'pending', 'on_hold'];

/** Summarises the SLA clock that matters next: first response until responded, then resolution. */
export function slaState(ticket: Pick<Ticket, 'status' | 'sla'>, now: number = Date.now()): SlaState {
  const sla: Sla | undefined = ticket.sla;
  if (!sla) return { tone: 'none', label: 'No SLA' };
  if (!ACTIVE.includes(ticket.status)) {
    return sla.resolutionBreached ? { tone: 'breached', label: 'Resolved late' } : { tone: 'met', label: 'SLA met' };
  }
  const responseMs = new Date(sla.firstResponseDueAt).getTime();
  const resolutionMs = new Date(sla.resolutionDueAt).getTime();
  const responding = !sla.firstRespondedAt;
  if (responding && (sla.firstResponseBreached || responseMs < now)) {
    return { tone: 'breached', label: 'Response overdue' };
  }
  if (sla.resolutionBreached || resolutionMs < now) return { tone: 'breached', label: 'Resolution overdue' };
  const due = responding ? sla.firstResponseDueAt : sla.resolutionDueAt;
  const dueMs = responding ? responseMs : resolutionMs;
  const what = responding ? 'Response' : 'Resolution';
  return { tone: dueMs - now < 60 * 60_000 ? 'warn' : 'ok', label: `${what} due ${relativeTime(due, now)}` };
}
