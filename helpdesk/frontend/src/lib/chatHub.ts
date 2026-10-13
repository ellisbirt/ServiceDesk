import { HttpTransportType, HubConnectionBuilder, HubConnectionState, LogLevel, Subject } from '@microsoft/signalr';
import { baseUrl, type ChatAck, type ChatStreamEvent } from './api';
import { devHubQuery, isEntraConfigured } from './auth';
import { getHubTicket } from './hubTicket';
import { needsSplitting, toParts, type ChatRequestPart, type ChatSendRequest } from './chatRequest';

export type { ChatSendRequest } from './chatRequest';

/**
 * Asynchronous chat over SignalR. `send` returns as soon as the server has queued the message; the reply
 * (streamed text, rate-limit notices, heartbeats and the final result) then arrives through `onEvent`.
 * A request too big for one hub message is uploaded in parts, so a long conversation cannot overflow the limit.
 */
export interface ChatHub {
  /** Opens the connection if needed. Rejects when SignalR cannot be used at all (e.g. blocked by a proxy). */
  connect(): Promise<void>;
  send(request: ChatSendRequest): Promise<ChatAck>;
  /** Asks the server to stop this conversation's request, so a reply nobody will read is not paid for. Never rejects. */
  cancel(conversationId: string): Promise<boolean>;
  onEvent(handler: (event: ChatStreamEvent) => void): () => void;
  /** Fires when the connection drops. Events sent while it was down are not replayed. */
  onConnectionLost(handler: () => void): () => void;
}

// Must stay at least twice the server's keep-alive (Chat:KeepAliveSeconds, 15s by default).
const SERVER_TIMEOUT_MS = 60_000;

function createChatHub(): ChatHub {
  const eventHandlers = new Set<(event: ChatStreamEvent) => void>();
  const lostHandlers = new Set<() => void>();

  const connection = new HubConnectionBuilder()
    .withUrl(`${baseUrl}/hubs/chat${devHubQuery}`, {
      // With Entra the factory returns a one-use ticket, never the access token, because this value ends up in the
      // WebSocket URL (see hubTicket.ts). Without Entra (local dev auth) there is no credential to send.
      accessTokenFactory: async () => (isEntraConfigured ? await getHubTicket() : ''),
      // WebSockets only: the other transports reuse one credential for many requests (a single-use ticket would
      // fail) or put it in the URL on every call. If WebSockets are blocked, App falls back to the REST endpoint.
      transport: HttpTransportType.WebSockets,
      withCredentials: false,
    })
    .withAutomaticReconnect()
    .withServerTimeout(SERVER_TIMEOUT_MS)
    .configureLogging(LogLevel.Warning)
    .build();

  connection.on('chatEvent', (event: ChatStreamEvent) => eventHandlers.forEach((handler) => handler(event)));
  const lost = () => lostHandlers.forEach((handler) => handler());
  connection.onreconnecting(lost);
  connection.onclose(lost);

  let starting: Promise<void> | undefined;

  return {
    async connect() {
      if (connection.state === HubConnectionState.Connected) return;
      if (connection.state !== HubConnectionState.Disconnected) {
        throw new Error('The chat connection is re-establishing.');
      }
      starting ??= connection.start().finally(() => {
        starting = undefined;
      });
      await starting;
    },
    send(request) {
      if (!needsSplitting(request)) return connection.invoke<ChatAck>('Send', request);

      // The client subscribes to the subject while `invoke` runs, so the parts are pushed right after it is called.
      const parts = new Subject<ChatRequestPart>();
      const ack = connection.invoke<ChatAck>('SendStreamed', parts);
      for (const part of toParts(request)) parts.next(part);
      parts.complete();
      return ack;
    },
    async cancel(conversationId) {
      // Nothing to cancel if the connection is not up; the server cancels on its own when a connection closes.
      if (connection.state !== HubConnectionState.Connected) return false;
      try {
        return await connection.invoke<boolean>('Cancel', conversationId);
      } catch {
        return false;
      }
    },
    onEvent(handler) {
      eventHandlers.add(handler);
      return () => eventHandlers.delete(handler);
    },
    onConnectionLost(handler) {
      lostHandlers.add(handler);
      return () => lostHandlers.delete(handler);
    },
  };
}

let instance: ChatHub | undefined;

/** The shared connection for the page. Created on first use so importing this module has no side effects. */
export function getChatHub(): ChatHub {
  return (instance ??= createChatHub());
}
