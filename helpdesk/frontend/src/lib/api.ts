export type ActionLane = 'POLICY_VIOLATION' | 'PUBLIC_TECH' | 'PUBLIC_SEARCH' | 'PRIVATE_CORP' | 'NO_MATCH';

// Shared contract mapping the API output
export interface HelpdeskApiResponse {
  conversationId: string;
  actionLane: ActionLane;
  responseText: string;
  metadata: Record<string, string>;
  /** Signature of the user's message in this turn. Store it with that message and send it back as history. */
  userSig?: string;
  /** Signature of `responseText`. Store it with the assistant message and send it back as history. */
  assistantSig?: string;
}

export type AttachmentKind = 'text' | 'image' | 'pdf' | 'office';

/**
 * A file uploaded to a conversation (mirrors AttachmentRef in Helpdesk.Shared). Send it back unchanged on the message
 * that uses it, and on that message in the stored history. The server checks `token` and ignores the other fields.
 */
export interface AttachmentRef {
  id: string;
  name: string;
  contentType: string;
  size: number;
  sha256?: string;
  kind: AttachmentKind;
  token: string;
}

/** Limits the server enforces on uploads, from GET /api/v1/chat/attachments/config. */
export interface AttachmentConfig {
  /** False when the server has attachments switched off: the attach button, drag and drop and paste conversion are hidden. */
  enabled: boolean;
  maxFileBytes: number;
  maxFilesPerMessage: number;
  maxFilesPerConversation: number;
  maxTotalBytesPerConversation: number;
  /** Lower-case, with the dot: ".png". */
  allowedExtensions: string[];
  /** Pasted text at least this long (characters) or this tall (lines) becomes an attachment. */
  pasteMinChars: number;
  pasteMinLines: number;
}

export interface ChatHistoryMessage {
  role: 'user' | 'assistant';
  content: string;
  /**
   * Proof from the server that it produced (or received) exactly this message at this point in this conversation.
   * Send it back unchanged: history without a valid signature is not trusted by the server and is dropped.
   */
  sig?: string;
  /** Files attached to this message; keep them on the stored message and send them back. */
  attachments?: AttachmentRef[];
}

export const MAX_MESSAGE_LENGTH = 4000;
export const MAX_HISTORY_MESSAGES = 20;

/** Server-to-client messages on the chat hub (mirrors ChatStreamEvent in Helpdesk.Shared). */
export type ChatStreamEventType = 'started' | 'delta' | 'rateLimited' | 'heartbeat' | 'completed' | 'failed';

export interface ChatStreamEvent {
  type: ChatStreamEventType;
  conversationId: string;
  requestId: string;
  text?: string;
  retryAfterSeconds?: number;
  attempt?: number;
  elapsedSeconds?: number;
  response?: HelpdeskApiResponse;
  error?: string;
  errorCode?: string;
}

/** Result of invoking Send on the hub: queued (requestId) or rejected (errorCode and error). */
export interface ChatAck {
  requestId?: string;
  errorCode?: string;
  error?: string;
}

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly errorCode?: string,
    public readonly retryAfterSeconds?: number,
  ) {
    super(message);
  }
}

export const baseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? '';

export async function sendChatMessage(
  messageText: string,
  conversationId: string,
  conversationHistory: ChatHistoryMessage[],
  authHeaders: () => Promise<Record<string, string>>,
  signal?: AbortSignal,
  attachments: AttachmentRef[] = [],
): Promise<HelpdeskApiResponse> {
  const res = await fetch(`${baseUrl}/api/v1/chat/message`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify({
      messageText,
      conversationId,
      conversationHistory,
      ...(attachments.length > 0 ? { attachments } : {}),
    }),
    signal,
  });

  if (!res.ok) {
    const problem = await errorDetails(res);
    throw new ApiError(problem.message, res.status, problem.code, readRetryAfter(res.headers.get('Retry-After')));
  }
  return (await res.json()) as HelpdeskApiResponse;
}

/** Metadata key set to 'true' on the reply that raised a service-desk ticket. */
export const TICKET_RAISED_METADATA_KEY = 'ticketRaised';

/** Metadata key set to 'true' on a reply that shows a ticket draft and waits for the user to confirm it. */
export const TICKET_PENDING_METADATA_KEY = 'ticketPending';

export async function probeServiceReadiness(signal?: AbortSignal): Promise<boolean> {
  try {
    const response = await fetch(`${baseUrl}/readyz`, {
      method: 'GET',
      cache: 'no-store',
      signal,
    });
    return response.ok;
  } catch {
    return false;
  }
}

export function readRetryAfter(value: string | null): number | undefined {
  if (!value?.trim()) return undefined;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0) return seconds;
  if (/^[+-]?\d/.test(value)) return undefined;
  const date = Date.parse(value);
  return Number.isFinite(date) ? Math.max(0, (date - Date.now()) / 1000) : undefined;
}

async function errorDetails(res: Response): Promise<{ message: string; code?: string }> {
  if (res.status === 401) return { message: 'Your session has expired. Please sign in again.' };
  if (res.status === 429) return { message: 'You are sending messages too quickly. Please wait a moment.' };
  try {
    const problem = (await res.json()) as { title?: string; code?: string; errors?: Record<string, string[]> };
    const first = problem.errors ? Object.values(problem.errors).flat()[0] : undefined;
    return { message: first ?? problem.title ?? `Request failed (${res.status}).`, code: problem.code };
  } catch {
    return { message: `Request failed (${res.status}).` };
  }
}

// ----- Attachments -----

const ATTACHMENT_ERRORS: Record<string, string> = {
  file_too_large: 'That file is larger than the allowed size.',
  unsupported_type: 'That type of file is not supported.',
  too_many_files: 'Too many files are attached.',
  conversation_quota_exceeded: 'This conversation has reached its limit for attachments.',
  unreadable_file: 'The file could not be read. It may be damaged or password protected.',
  rate_limited: 'You are uploading too quickly. Please wait a moment.',
};

/** An upload the server rejected, with a message that is safe to show. */
export class AttachmentUploadError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code?: string,
  ) {
    super(message);
  }
}

export function attachmentErrorMessage(status: number, code?: string): string {
  if (code && ATTACHMENT_ERRORS[code]) return ATTACHMENT_ERRORS[code];
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 413) return ATTACHMENT_ERRORS.file_too_large;
  if (status === 415) return ATTACHMENT_ERRORS.unsupported_type;
  if (status === 429) return ATTACHMENT_ERRORS.rate_limited;
  if (status === 422) return ATTACHMENT_ERRORS.unreadable_file;
  return 'The file could not be uploaded. Please try again.';
}

/** The upload limits as sent by the server, or null when they cannot be read (the caller then uses its defaults). Never rejects. */
export async function fetchAttachmentConfig(
  authHeaders: () => Promise<Record<string, string>>,
  signal?: AbortSignal,
): Promise<unknown> {
  try {
    const res = await fetch(`${baseUrl}/api/v1/chat/attachments/config`, { headers: await authHeaders(), signal });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

/**
 * Uploads one file to a conversation. XMLHttpRequest rather than fetch so progress can be reported. The conversation id
 * is sent before the file so the server can check it before reading the content.
 */
export async function uploadAttachment(
  file: File,
  conversationId: string,
  authHeaders: () => Promise<Record<string, string>>,
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<AttachmentRef> {
  const headers = await authHeaders();
  return new Promise<AttachmentRef>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Upload cancelled', 'AbortError'));
      return;
    }
    const form = new FormData();
    form.append('conversationId', conversationId);
    form.append('file', file, file.name);

    const xhr = new XMLHttpRequest();
    xhr.open('POST', `${baseUrl}/api/v1/chat/attachments`);
    for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && e.total > 0) onProgress?.(e.loaded / e.total);
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as AttachmentRef);
        } catch {
          reject(new AttachmentUploadError(attachmentErrorMessage(500), xhr.status));
        }
        return;
      }
      let code: string | undefined;
      try {
        code = (JSON.parse(xhr.responseText) as { code?: string }).code;
      } catch {
        // Not a problem document; the status alone decides the message.
      }
      reject(new AttachmentUploadError(attachmentErrorMessage(xhr.status, code), xhr.status, code));
    };
    xhr.onerror = () => reject(new AttachmentUploadError(attachmentErrorMessage(0), 0));
    xhr.onabort = () => reject(new DOMException('Upload cancelled', 'AbortError'));
    signal?.addEventListener('abort', () => xhr.abort(), { once: true });
    xhr.send(form);
  });
}

/** Removes an uploaded file that will not be sent. Best effort: a failure only leaves the file to expire on its own. */
export async function deleteAttachment(
  id: string,
  conversationId: string,
  authHeaders: () => Promise<Record<string, string>>,
): Promise<void> {
  try {
    const url = `${baseUrl}/api/v1/chat/attachments/${encodeURIComponent(id)}?conversationId=${encodeURIComponent(conversationId)}`;
    await fetch(url, { method: 'DELETE', headers: await authHeaders() });
  } catch {
    // Nothing to do: the server removes unused uploads after its retention period.
  }
}
