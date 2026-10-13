import { getAuthHeaders } from './auth';
import type {
  AddCommentInput,
  AddCommentResult,
  AssistInfo,
  Attachment,
  ClientConfig,
  CreateAttachmentResult,
  CreateTicketInput,
  DraftKbResult,
  DraftReplyResult,
  ImproveMode,
  ImproveResult,
  KbArticle,
  KbArticleInput,
  KbQuery,
  KbVersion,
  Me,
  Page,
  ReplyWithKbResult,
  SuggestSolutionsResult,
  Ticket,
  TicketQuery,
  TimelineEntry,
  TransitionInput,
  UpdateTicketInput,
  Visibility,
  Vocabulary,
} from './types';

export const baseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? '';
const API = `${baseUrl}/api/v1`;

/** An API failure, built from the RFC 7807 problem details the API returns. */
export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code?: string,
    public readonly correlationId?: string,
    public readonly fieldErrors?: Record<string, string[]>,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** The document changed since it was loaded (If-Match failed). */
  get isConflict(): boolean {
    return this.status === 412 || this.status === 409;
  }
}

interface RequestOptions {
  method?: string;
  body?: unknown;
  /** ETag to send as If-Match, exactly as the API returned it. */
  ifMatch?: string;
  signal?: AbortSignal;
}

function newCorrelationId(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}`;
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = {
    Accept: 'application/json',
    'X-Correlation-ID': newCorrelationId(),
    ...(await getAuthHeaders()),
  };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (options.ifMatch) headers['If-Match'] = options.ifMatch;

  const res = await fetch(`${API}${path}`, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
    cache: 'no-store',
  });
  if (!res.ok) throw await toApiError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export async function toApiError(res: Response): Promise<ApiError> {
  const headerCorrelation = res.headers.get('X-Correlation-ID') ?? undefined;
  let problem: {
    title?: string;
    detail?: string;
    code?: string;
    correlationId?: string;
    errors?: Record<string, string[]>;
  } = {};
  try {
    problem = await res.json();
  } catch {
    // Not a problem-details body.
  }
  const firstFieldError = problem.errors ? Object.values(problem.errors).flat()[0] : undefined;
  const message =
    res.status === 401
      ? 'Your session has expired. Sign in again to continue.'
      : res.status === 403
        ? 'You do not have permission to do that.'
        : res.status === 412
          ? 'Someone else changed this item. Reload it and try again.'
          : (problem.detail ?? firstFieldError ?? problem.title ?? `Request failed (${res.status}).`);
  return new ApiError(message, res.status, problem.code, problem.correlationId ?? headerCorrelation, problem.errors);
}

function query(params: object): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '' || value === false) continue;
    search.set(key, String(value));
  }
  const s = search.toString();
  return s ? `?${s}` : '';
}

const enc = encodeURIComponent;

export const api = {
  config: () => request<ClientConfig>('/meta/config'),
  vocabulary: () => request<Vocabulary>('/meta/vocabulary'),
  me: () => request<Me>('/me'),

  listTickets: (q: TicketQuery, signal?: AbortSignal) => request<Page<Ticket>>(`/tickets${query(q)}`, { signal }),
  getTicket: (id: string) => request<Ticket>(`/tickets/${enc(id)}`),
  createTicket: (body: CreateTicketInput) => request<Ticket>('/tickets', { method: 'POST', body }),
  updateTicket: (id: string, body: UpdateTicketInput, ifMatch?: string) =>
    request<Ticket>(`/tickets/${enc(id)}`, { method: 'PATCH', body, ifMatch }),
  assignTicket: (
    id: string,
    body: { assignmentGroup?: string; assignee?: { id: string; displayName: string; email?: string } },
    ifMatch?: string,
  ) => request<Ticket>(`/tickets/${enc(id)}/assign`, { method: 'POST', body, ifMatch }),
  transitionTicket: (id: string, body: TransitionInput, ifMatch?: string) =>
    request<Ticket>(`/tickets/${enc(id)}/transition`, { method: 'POST', body, ifMatch }),
  timeline: (id: string) => request<TimelineEntry[]>(`/tickets/${enc(id)}/timeline`),
  addComment: (id: string, body: AddCommentInput) =>
    request<AddCommentResult>(`/tickets/${enc(id)}/comments`, { method: 'POST', body }),

  listAttachments: (id: string) => request<Attachment[]>(`/tickets/${enc(id)}/attachments`),
  createAttachment: (id: string, body: { fileName: string; contentType: string; size: number; sha256?: string }) =>
    request<CreateAttachmentResult>(`/tickets/${enc(id)}/attachments`, { method: 'POST', body }),
  completeAttachment: (id: string, attachmentId: string) =>
    request<Attachment>(`/tickets/${enc(id)}/attachments/${enc(attachmentId)}/complete`, { method: 'POST' }),
  downloadAttachment: (id: string, attachmentId: string) =>
    request<{ downloadUrl: string; expiresAt: string }>(`/tickets/${enc(id)}/attachments/${enc(attachmentId)}/download`),

  assistInfo: () => request<AssistInfo>('/assist'),
  suggestSolutions: (id: string, instructions?: string) =>
    request<SuggestSolutionsResult>(`/tickets/${enc(id)}/assist/suggest-solutions`, {
      method: 'POST',
      body: { instructions },
    }),
  draftReply: (id: string, body: { instructions?: string; visibility?: Visibility; kbArticleIds?: string[] }) =>
    request<DraftReplyResult>(`/tickets/${enc(id)}/assist/draft-reply`, { method: 'POST', body }),
  draftKb: (id: string, instructions?: string) =>
    request<DraftKbResult>(`/tickets/${enc(id)}/assist/draft-kb`, { method: 'POST', body: { instructions } }),
  replyWithKb: (id: string, instructions?: string) =>
    request<ReplyWithKbResult>(`/tickets/${enc(id)}/assist/reply-with-kb`, { method: 'POST', body: { instructions } }),
  improve: (id: string, body: { text: string; mode: ImproveMode; instructions?: string }) =>
    request<ImproveResult>(`/tickets/${enc(id)}/assist/improve`, { method: 'POST', body }),

  listKb: (q: KbQuery, signal?: AbortSignal) => request<Page<KbArticle>>(`/kb/articles${query(q)}`, { signal }),
  getKb: (id: string) => request<KbArticle>(`/kb/articles/${enc(id)}`),
  kbVersions: (id: string) => request<KbVersion[]>(`/kb/articles/${enc(id)}/versions`),
  createKb: (body: KbArticleInput) => request<KbArticle>('/kb/articles', { method: 'POST', body }),
  updateKb: (id: string, body: KbArticleInput, ifMatch?: string) =>
    request<KbArticle>(`/kb/articles/${enc(id)}`, { method: 'PUT', body, ifMatch }),
  kbAction: (id: string, action: 'publish' | 'archive' | 'restore' | 'reembed', ifMatch?: string) =>
    request<KbArticle>(`/kb/articles/${enc(id)}/${action}`, { method: 'POST', ifMatch }),
};

/** Hex SHA-256 of a file, computed in the browser so the API can verify the upload. */
export async function sha256Hex(file: Blob): Promise<string | undefined> {
  if (typeof crypto === 'undefined' || !crypto.subtle) return undefined;
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * Uploads a file as a ticket attachment: reserve it (API), PUT the bytes straight to the signed blob URL, then tell
 * the API the upload is complete so it can verify the size and hash.
 */
export async function uploadAttachment(ticketId: string, file: File): Promise<Attachment> {
  const contentType = file.type || 'application/octet-stream';
  const sha256 = await sha256Hex(file);
  const reserved = await api.createAttachment(ticketId, { fileName: file.name, contentType, size: file.size, sha256 });
  const put = await fetch(reserved.uploadUrl, {
    method: 'PUT',
    headers: { 'x-ms-blob-type': 'BlockBlob', 'Content-Type': contentType, ...reserved.headers },
    body: file,
  });
  if (!put.ok) throw new ApiError(`Uploading ${file.name} failed (${put.status}).`, put.status);
  return api.completeAttachment(ticketId, reserved.attachment.id);
}
