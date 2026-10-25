import type { AttachmentRef, ChatHistoryMessage } from './api';

export interface ChatSendRequest {
  messageText: string;
  conversationId: string;
  conversationHistory: ChatHistoryMessage[];
  /** Files attached to this message. Left out when there are none. */
  attachments?: AttachmentRef[];
}

/**
 * One piece of a request uploaded with the hub's `SendStreamed` method (mirrors ChatRequestPart in Helpdesk.Shared).
 * The first part carries `conversationId` and `messageText`; every later part carries one `history` message.
 */
export interface ChatRequestPart {
  conversationId?: string;
  messageText?: string;
  /** First part only. */
  attachments?: AttachmentRef[];
  history?: ChatHistoryMessage;
}

/**
 * SignalR closes the connection when one hub message is larger than the server's limit (Chat:MaxReceiveMessageBytes,
 * 32 KB by default). A request at or under this many bytes is sent whole; a bigger one is split into parts, each its
 * own message. Keep this comfortably below the server's limit.
 */
export const SINGLE_MESSAGE_LIMIT_BYTES = 24_000;

const encoder = new TextEncoder();

/** The size of the JSON the SignalR client would send, in UTF-8 bytes. */
export function encodedSize(value: unknown): number {
  return encoder.encode(JSON.stringify(value)).length;
}

export function needsSplitting(request: ChatSendRequest): boolean {
  return encodedSize(request) > SINGLE_MESSAGE_LIMIT_BYTES;
}

/** The request as parts, in the order the server expects: the header, then the history oldest first. */
export function toParts(request: ChatSendRequest): ChatRequestPart[] {
  return [
    {
      conversationId: request.conversationId,
      messageText: request.messageText,
      ...(request.attachments?.length ? { attachments: request.attachments } : {}),
    },
    ...request.conversationHistory.map((history): ChatRequestPart => ({ history })),
  ];
}
