import {
  HubConnection,
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from '@microsoft/signalr';
import type { QueryClient } from '@tanstack/react-query';
import { baseUrl } from './api';
import { devHubQuery, getAccessToken } from './auth';
import type { KbArticle, Ticket } from './types';
import { keys } from './queries';

/**
 * A browser cannot set an Authorization header on a WebSocket, so with Entra sign-in the access token is exchanged
 * (in a header) for a single-use, seconds-long ticket and only the ticket goes in the URL. SignalR asks for a
 * credential for negotiate and again for the socket, so each connection buys two tickets.
 */
export async function getHubTicket(): Promise<string> {
  const token = await getAccessToken();
  if (!token) throw new Error('Not signed in.');
  const res = await fetch(`${baseUrl}/api/v1/hub/ticket`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
    cache: 'no-store',
  });
  if (!res.ok) throw new Error(`Could not get a live-update ticket (${res.status}).`);
  const body = (await res.json()) as { ticket?: string };
  if (!body.ticket) throw new Error('The live-update ticket was missing from the response.');
  return body.ticket;
}

export type HubState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/**
 * Connects to /hubs/tickets and keeps the TanStack Query cache fresh: pushed tickets are written straight into the
 * cache and the lists are invalidated. After a reconnect everything is refetched, because events may have been missed.
 */
export function connectTicketsHub(
  queryClient: QueryClient,
  hubTicketRequired: boolean,
  onState: (state: HubState) => void,
): () => void {
  const connection: HubConnection = new HubConnectionBuilder()
    .withUrl(`${baseUrl}/hubs/tickets${hubTicketRequired ? '' : devHubQuery}`, {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: false,
      accessTokenFactory: hubTicketRequired ? getHubTicket : undefined,
    })
    .withAutomaticReconnect()
    .withServerTimeout(60_000)
    .configureLogging(LogLevel.Warning)
    .build();

  connection.on('ticketCreated', (ticket: Ticket) => {
    queryClient.setQueryData(keys.ticket(ticket.id), ticket);
    void queryClient.invalidateQueries({ queryKey: keys.ticketLists });
  });
  connection.on('ticketUpdated', (ticket: Ticket) => {
    queryClient.setQueryData(keys.ticket(ticket.id), ticket);
    void queryClient.invalidateQueries({ queryKey: keys.ticketLists });
  });
  connection.on('timelineEntryAdded', (ticketId: string) => {
    void queryClient.invalidateQueries({ queryKey: keys.timeline(ticketId) });
    void queryClient.invalidateQueries({ queryKey: keys.attachments(ticketId) });
  });
  connection.on('kbChanged', (article: KbArticle) => {
    queryClient.setQueryData(keys.kbArticle(article.id), article);
    void queryClient.invalidateQueries({ queryKey: keys.kbLists });
    void queryClient.invalidateQueries({ queryKey: keys.kbVersions(article.id) });
  });

  connection.onreconnecting(() => onState('reconnecting'));
  connection.onreconnected(() => {
    onState('connected');
    void queryClient.invalidateQueries();
  });
  connection.onclose(() => onState('disconnected'));

  let stopped = false;
  onState('connecting');
  connection
    .start()
    .then(() => {
      if (stopped) void connection.stop();
      else onState('connected');
    })
    .catch(() => {
      if (!stopped) onState('disconnected');
    });

  return () => {
    stopped = true;
    void connection.stop();
  };
}
