import { afterEach, describe, expect, it, vi } from 'vitest';
import { json, mockApi } from '../test/fixtures';
import { ApiError, api, toApiError, uploadAttachment } from './api';

vi.mock('./auth', () => ({
  getAuthHeaders: async () => ({ 'X-Dev-Tenant': 'dev-tenant', 'X-Dev-User': 'dev-user' }),
}));

afterEach(() => vi.unstubAllGlobals());

const problem = (status: number, body: object) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json' } });

describe('toApiError', () => {
  it('prefers the problem detail, then the first field error, then the title', async () => {
    expect((await toApiError(problem(400, { title: 'Bad', detail: 'Subject is too long.' }))).message).toBe('Subject is too long.');
    expect((await toApiError(problem(400, { title: 'Bad', errors: { subject: ['Subject is required.'] } }))).message).toBe(
      'Subject is required.',
    );
    expect((await toApiError(problem(400, { title: 'Bad request' }))).message).toBe('Bad request');
    expect((await toApiError(new Response('oops', { status: 502 }))).message).toBe('Request failed (502).');
  });

  it('uses fixed, actionable messages for auth and concurrency failures', async () => {
    expect((await toApiError(problem(401, { detail: 'x' }))).message).toMatch(/session has expired/);
    expect((await toApiError(problem(403, { detail: 'x' }))).message).toBe('You do not have permission to do that.');
    const conflict = await toApiError(problem(412, { detail: 'x' }));
    expect(conflict.message).toBe('Someone else changed this item. Reload it and try again.');
    expect(conflict.isConflict).toBe(true);
    expect((await toApiError(problem(409, {}))).isConflict).toBe(true);
    expect((await toApiError(problem(400, {}))).isConflict).toBe(false);
  });

  it('keeps the correlation id so users can quote it', async () => {
    const res = new Response('{}', { status: 500, headers: { 'X-Correlation-ID': 'corr-1' } });
    expect((await toApiError(res)).correlationId).toBe('corr-1');
  });
});

describe('api requests', () => {
  it('sends dev identity, a correlation id and If-Match on concurrency-checked writes', async () => {
    const { calls } = mockApi({ 'PATCH /tickets/t-1': () => ({ id: 't-1' }) });
    await api.updateTicket('t-1', { subject: 'New subject' }, '"etag-1"');
    expect(calls[0].headers['if-match']).toBe('"etag-1"');
    expect(calls[0].headers['x-dev-tenant']).toBe('dev-tenant');
    expect(calls[0].headers['x-correlation-id']).toBeTruthy();
    expect(calls[0].body).toEqual({ subject: 'New subject' });
  });

  it('drops empty filters from list queries', async () => {
    const { calls } = mockApi({ 'GET /tickets': () => ({ items: [] }) });
    await api.listTickets({ status: 'open', q: '', unassigned: false, pageSize: 25 });
    expect(calls[0].search).toBe('?status=open&pageSize=25');
  });

  it('posts KB lifecycle actions with the article ETag', async () => {
    const { calls } = mockApi({ 'POST /kb/articles/kb-1/publish': () => ({ id: 'kb-1' }) });
    await api.kbAction('kb-1', 'publish', '"kb-etag"');
    expect(calls[0].headers['if-match']).toBe('"kb-etag"');
  });

  it('turns a failed response into an ApiError', async () => {
    mockApi({ 'GET /tickets/missing': () => json({ title: 'Not found', detail: 'Ticket missing was not found.' }, 404) });
    await expect(api.getTicket('missing')).rejects.toMatchObject({ status: 404, message: 'Ticket missing was not found.' });
  });
});

describe('uploadAttachment', () => {
  const reserved = {
    attachment: { id: 'a-1', ticketId: 't-1', fileName: 'log.txt', contentType: 'text/plain', size: 5, status: 'pending_upload', createdAt: '' },
    uploadUrl: 'https://blob.example/attachments/a-1?sig=x',
    expiresAt: '',
    headers: { 'x-ms-meta-ticket': 't-1' },
  };

  it('reserves, PUTs the bytes to the signed URL, then completes', async () => {
    const { calls } = mockApi({
      'POST /tickets/t-1/attachments': () => reserved,
      'PUT /attachments/a-1': () => new Response(null, { status: 201 }),
      'POST /tickets/t-1/attachments/a-1/complete': () => ({ ...reserved.attachment, status: 'available' }),
    });
    const result = await uploadAttachment('t-1', new File(['hello'], 'log.txt', { type: 'text/plain' }));

    expect(result.status).toBe('available');
    expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual([
      'POST /tickets/t-1/attachments',
      'PUT /attachments/a-1',
      'POST /tickets/t-1/attachments/a-1/complete',
    ]);
    expect(calls[0].body).toMatchObject({ fileName: 'log.txt', contentType: 'text/plain', size: 5 });
    expect(calls[1].headers['x-ms-blob-type']).toBe('BlockBlob');
    expect(calls[1].headers['x-ms-meta-ticket']).toBe('t-1');
  });

  it('reports which file failed when the blob upload is rejected', async () => {
    mockApi({
      'POST /tickets/t-1/attachments': () => reserved,
      'PUT /attachments/a-1': () => new Response(null, { status: 403 }),
    });
    const err = await uploadAttachment('t-1', new File(['hello'], 'log.txt')).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).message).toBe('Uploading log.txt failed (403).');
  });
});
