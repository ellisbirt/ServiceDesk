import { vi } from 'vitest';
import type { AssistInfo, KbArticle, Me, Ticket, TimelineEntry, Vocabulary } from '../lib/types';

const NOW = new Date().toISOString();
const inHours = (h: number) => new Date(Date.now() + h * 3_600_000).toISOString();

export const agent = { id: 'dev-user', displayName: 'Dev Agent' };

export const vocabulary: Vocabulary = {
  ticketStatuses: ['new', 'open', 'pending', 'on_hold', 'resolved', 'closed', 'cancelled'],
  activeStatuses: ['new', 'open', 'pending', 'on_hold'],
  transitions: {
    new: ['open', 'pending', 'resolved', 'cancelled'],
    open: ['pending', 'on_hold', 'resolved', 'cancelled'],
    pending: ['open', 'resolved'],
    on_hold: ['open'],
    resolved: ['open', 'closed'],
    closed: [],
    cancelled: [],
  },
  pendingReasons: ['awaiting_requester', 'awaiting_vendor'],
  ticketTypes: ['incident', 'service_request', 'question', 'problem'],
  channels: ['portal', 'email', 'chat', 'agent'],
  priorities: ['P1', 'P2', 'P3', 'P4'],
  categories: ['access', 'hardware', 'network', 'software'],
  resolutionCodes: ['fixed', 'workaround', 'duplicate'],
  visibilities: ['public', 'internal'],
  kbStatuses: ['draft', 'published', 'archived'],
  impactLevels: [1, 2, 3],
  urgencyLevels: [1, 2, 3],
};

export const me: Me = { tenantId: 'dev-tenant', userId: 'dev-user', displayName: 'Dev Agent', roles: ['agent'] };

export const assistInfo: AssistInfo = {
  available: true,
  provider: 'fake',
  model: 'fake-model',
  offline: true,
  features: ['suggest-solutions', 'draft-reply', 'draft-kb', 'reply-with-kb', 'improve'],
  improveModes: ['polish', 'shorten'],
  kbPlaceholder: '{{KB}}',
};

export function ticket(overrides: Partial<Ticket> = {}): Ticket {
  return {
    id: 't-1',
    tenantId: 'dev-tenant',
    createdAt: NOW,
    updatedAt: NOW,
    _etag: '"etag-1"',
    number: 'INC-1001',
    subject: 'VPN drops every few minutes',
    description: 'Since this morning the VPN disconnects every 5 minutes.',
    type: 'incident',
    status: 'open',
    impact: 2,
    urgency: 2,
    priority: 'P3',
    category: 'network',
    requester: { id: 'u-1', displayName: 'Alex Requester', email: 'alex@example.com' },
    channel: 'portal',
    sla: {
      policyId: 'default',
      firstResponseDueAt: inHours(4),
      resolutionDueAt: inHours(24),
      firstResponseBreached: false,
      resolutionBreached: false,
    },
    ...overrides,
  };
}

export function kbArticle(overrides: Partial<KbArticle> = {}): KbArticle {
  return {
    id: 'kb-1',
    number: 'KB-1001',
    title: 'Fix VPN disconnects',
    summary: 'Update the VPN client to stop drop-outs.',
    body: 'Install version 5.2 of the VPN client.',
    status: 'draft',
    visibility: 'public',
    version: 1,
    createdAt: NOW,
    updatedAt: NOW,
    embedding: { status: 'ready', provider: 'fake', model: 'fake-embed', dimensions: 64, embeddedAt: NOW },
    eTag: '"kb-etag-1"',
    ...overrides,
  };
}

export const comment = (overrides: Partial<TimelineEntry> = {}): TimelineEntry => ({
  id: 'c-1',
  docType: 'comment',
  ticketId: 't-1',
  author: agent,
  createdAt: NOW,
  visibility: 'public',
  body: 'We are looking into it.',
  ...overrides,
});

export interface RecordedCall {
  method: string;
  path: string;
  search: string;
  headers: Record<string, string>;
  body: unknown;
}

type Handler = (call: RecordedCall) => unknown | Response | Promise<unknown | Response>;

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

/**
 * Stubs global fetch with a tiny router keyed by "METHOD /path" (the path after /api/v1, without the query).
 * Unmatched requests get a 404 problem so a missing route shows up as a visible error rather than a hang.
 */
export function mockApi(routes: Record<string, Handler>) {
  const calls: RecordedCall[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    const method = (init?.method ?? 'GET').toUpperCase();
    const path = url.pathname.replace(/^\/api\/v1/, '');
    const headers = Object.fromEntries(new Headers(init?.headers).entries());
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body;
    const call = { method, path, search: url.search, headers, body };
    calls.push(call);
    const handler = routes[`${method} ${path}`];
    if (!handler) return json({ title: 'Not found', detail: `No mock for ${method} ${path}` }, 404);
    const result = await handler(call);
    return result instanceof Response ? result : json(result ?? {});
  });
  vi.stubGlobal('fetch', fetchMock);
  return { calls, fetchMock, find: (method: string, path: string) => calls.filter((c) => c.method === method && c.path === path) };
}

/** Routes every page needs: metadata, the signed-in agent, and the AI assistant info. */
export const baseRoutes: Record<string, Handler> = {
  'GET /meta/vocabulary': () => vocabulary,
  'GET /me': () => me,
  'GET /assist': () => assistInfo,
  'GET /kb/articles': () => ({ items: [] }),
};
