export type ActionLane = 'POLICY_VIOLATION' | 'PUBLIC_TECH' | 'PUBLIC_SEARCH' | 'PRIVATE_CORP' | 'NO_MATCH';

// Shared contract mapping the API output
export interface HelpdeskApiResponse {
  conversationId: string;
  actionLane: ActionLane;
  responseText: string;
  actionCardRequired: boolean;
  intakeForm?: IntakeForm;
  metadata: Record<string, string>;
  /** Signature of the user's message in this turn. Store it with that message and send it back as history. */
  userSig?: string;
  /** Signature of `responseText`. Store it with the assistant message and send it back as history. */
  assistantSig?: string;
}

export interface IntakeForm {
  title: string;
  submitLabel: string;
  fields: IntakeFormField[];
}

export interface IntakeFormField {
  id: string;
  label: string;
  description: string;
  inputType: 'text' | 'textarea';
  required: boolean;
}

export interface ChatHistoryMessage {
  role: 'user' | 'assistant';
  content: string;
  /**
   * Proof from the server that it produced (or received) exactly this message at this point in this conversation.
   * Send it back unchanged: history without a valid signature is not trusted by the server and is dropped.
   */
  sig?: string;
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
): Promise<HelpdeskApiResponse> {
  const res = await fetch(`${baseUrl}/api/v1/chat/message`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify({ messageText, conversationId, conversationHistory }),
    signal,
  });

  if (!res.ok) {
    throw new ApiError(await errorMessage(res), res.status);
  }
  return (await res.json()) as HelpdeskApiResponse;
}

export const ESCALATION_TOKEN_METADATA_KEY = 'escalationToken';

export interface EscalationRequest {
  conversationId: string;
  escalationToken: string;
  formTitle: string;
  answers: { label: string; value: string }[];
}

export interface EscalationResponse {
  ticketEventId: string;
  status: string;
}

/** Sends a pending escalation and its completed form to the service desk as one ticket. */
export async function submitEscalation(
  request: EscalationRequest,
  authHeaders: () => Promise<Record<string, string>>,
  signal?: AbortSignal,
): Promise<EscalationResponse> {
  const res = await fetch(`${baseUrl}/api/v1/chat/escalate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(request),
    signal,
  });

  if (!res.ok) {
    throw new ApiError(await errorMessage(res), res.status);
  }
  return (await res.json()) as EscalationResponse;
}

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

async function errorMessage(res: Response): Promise<string> {
  if (res.status === 401) return 'Your session has expired. Please sign in again.';
  if (res.status === 429) return 'You are sending messages too quickly. Please wait a moment.';
  try {
    const problem = (await res.json()) as { title?: string; errors?: Record<string, string[]> };
    const first = problem.errors ? Object.values(problem.errors).flat()[0] : undefined;
    return first ?? problem.title ?? `Request failed (${res.status}).`;
  } catch {
    return `Request failed (${res.status}).`;
  }
}
