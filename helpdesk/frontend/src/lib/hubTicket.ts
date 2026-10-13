import { baseUrl } from './api';
import { getAccessToken } from './auth';

/**
 * A browser cannot set an Authorization header on a WebSocket, so the hub credential goes in the URL, where proxies
 * and load balancers log it. The access token is therefore never put there: it is sent in a header to this endpoint
 * (like any other API call) in exchange for a ticket that is valid for seconds and for one use, and only the ticket
 * goes in the URL. SignalR asks for a credential once for negotiate and again for the WebSocket, so each connection
 * (and each automatic reconnect) buys two tickets.
 */
export async function getHubTicket(): Promise<string> {
  const token = await getAccessToken();
  if (!token) throw new Error('Not signed in.');

  const res = await fetch(`${baseUrl}/api/v1/chat/ticket`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
    cache: 'no-store',
  });
  if (!res.ok) throw new Error(`Could not get a chat connection ticket (${res.status}).`);

  const body = (await res.json()) as { ticket?: string };
  if (!body.ticket) throw new Error('The chat connection ticket was missing from the response.');
  return body.ticket;
}
