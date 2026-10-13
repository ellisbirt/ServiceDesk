import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const auth = vi.hoisted(() => ({ getAccessToken: vi.fn<() => Promise<string | undefined>>() }));
vi.mock('./auth', () => auth);

import { getHubTicket } from './hubTicket';

const respond = (status: number, body?: unknown) =>
  vi.fn().mockResolvedValue(new Response(body === undefined ? null : JSON.stringify(body), { status }));

describe('getHubTicket', () => {
  beforeEach(() => {
    auth.getAccessToken.mockResolvedValue('the-access-token');
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    auth.getAccessToken.mockReset();
  });

  it('sends the access token in a header, never in the URL, and returns only the ticket', async () => {
    const fetchMock = respond(200, { ticket: 'one-use-ticket', expiresInSeconds: 30 });
    vi.stubGlobal('fetch', fetchMock);

    expect(await getHubTicket()).toBe('one-use-ticket');

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/v1/chat/ticket');
    expect(url).not.toContain('the-access-token');
    expect(init.method).toBe('POST');
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer the-access-token');
    expect(init.cache).toBe('no-store');
  });

  it('fails when the user is not signed in, without calling the server', async () => {
    auth.getAccessToken.mockResolvedValue(undefined);
    const fetchMock = respond(200, { ticket: 'x' });
    vi.stubGlobal('fetch', fetchMock);

    await expect(getHubTicket()).rejects.toThrow('Not signed in');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([401, 403, 429, 500])('fails on HTTP %i so the app can fall back to the REST endpoint', async (status) => {
    vi.stubGlobal('fetch', respond(status));
    await expect(getHubTicket()).rejects.toThrow(String(status));
  });

  it('fails when the response has no ticket', async () => {
    vi.stubGlobal('fetch', respond(200, {}));
    await expect(getHubTicket()).rejects.toThrow('missing');
  });
});
