import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page, type Request } from '@playwright/test';

const now = new Date().toISOString();
const inHours = (h: number) => new Date(Date.now() + h * 3_600_000).toISOString();

const vocabulary = {
  ticketStatuses: ['new', 'open', 'pending', 'on_hold', 'resolved', 'closed', 'cancelled'],
  activeStatuses: ['new', 'open', 'pending', 'on_hold'],
  transitions: { new: ['open', 'resolved'], open: ['pending', 'resolved'], pending: ['open'], on_hold: ['open'], resolved: ['open', 'closed'], closed: [], cancelled: [] },
  pendingReasons: ['awaiting_requester'],
  ticketTypes: ['incident', 'service_request'],
  channels: ['portal', 'agent'],
  priorities: ['P1', 'P2', 'P3', 'P4'],
  categories: ['network', 'software'],
  resolutionCodes: ['fixed', 'workaround'],
  visibilities: ['public', 'internal'],
  kbStatuses: ['draft', 'published', 'archived'],
  impactLevels: [1, 2, 3],
  urgencyLevels: [1, 2, 3],
};

const ticket = {
  id: 't-1',
  tenantId: 'dev-tenant',
  createdAt: now,
  updatedAt: now,
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
  sla: { policyId: 'default', firstResponseDueAt: inHours(4), resolutionDueAt: inHours(24), firstResponseBreached: false, resolutionBreached: false },
};

const aiAssisted = { provider: 'fake', model: 'fake-model', feature: 'reply-with-kb' };

function newArticle(body: Record<string, unknown>) {
  return {
    ...body,
    id: 'kb-1',
    number: 'KB-1001',
    status: 'draft',
    visibility: body.visibility ?? 'public',
    version: 1,
    createdAt: now,
    updatedAt: now,
    embedding: { status: 'ready', provider: 'fake', model: 'fake-embed', dimensions: 64, embeddedAt: now },
    eTag: '"kb-etag-1"',
  };
}

/** A stateful fake of the AgentDesk API: just enough to walk the ticket → KB-cited reply → KB flow. */
async function mockApi(page: Page) {
  const state = {
    timeline: [{ id: 'c-1', docType: 'comment', ticketId: 't-1', author: { id: 'dev-user', displayName: 'Dev Agent' }, createdAt: now, visibility: 'public', body: 'We are looking into it.' }] as Record<string, unknown>[],
    articles: [] as Record<string, unknown>[],
    posts: [] as { path: string; body: unknown; headers: Record<string, string> }[],
  };

  await page.route('**/hubs/**', (route) => route.abort());
  await page.route('**/api/v1/**', async (route) => {
    const req: Request = route.request();
    const url = new URL(req.url());
    const path = url.pathname.replace(/^\/api\/v1/, '');
    const method = req.method();
    const body = req.postData() ? req.postDataJSON() : undefined;
    if (method !== 'GET') state.posts.push({ path, body, headers: req.headers() });
    const ok = (data: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(data) });

    switch (`${method} ${path}`) {
      case 'GET /meta/vocabulary':
        return ok(vocabulary);
      case 'GET /me':
        return ok({ tenantId: 'dev-tenant', userId: 'dev-user', displayName: 'Dev Agent', roles: ['agent'] });
      case 'GET /assist':
        return ok({ available: true, provider: 'fake', model: 'fake-model', offline: true, features: [], improveModes: ['polish'], kbPlaceholder: '{{KB}}' });
      case 'GET /tickets':
        return ok({ items: [ticket] });
      case 'GET /tickets/t-1':
        return ok(ticket);
      case 'GET /tickets/t-1/timeline':
        return ok(state.timeline);
      case 'GET /tickets/t-1/attachments':
        return ok([]);
      case 'POST /tickets/t-1/assist/reply-with-kb':
        return ok({
          article: {
            title: 'Fix VPN disconnects',
            summary: 'Update the VPN client to stop drop-outs.',
            resolution: 'Install version 5.2 of the client.',
            body: '## Fix\n\nInstall version 5.2 of the VPN client.',
            visibility: 'public',
          },
          replyBody: 'Hi Alex, the steps in {{KB}} should fix this.',
          placeholder: '{{KB}}',
          aiAssisted,
        });
      case 'POST /kb/articles': {
        const article = newArticle(body as Record<string, unknown>);
        state.articles = [article];
        return ok(article);
      }
      case 'POST /tickets/t-1/comments': {
        const c = { id: `c-${state.timeline.length + 1}`, docType: 'comment', ticketId: 't-1', author: { id: 'dev-user', displayName: 'Dev Agent' }, createdAt: new Date().toISOString(), ...(body as object) };
        state.timeline = [...state.timeline, c];
        state.articles = state.articles.map((a) => ({ ...a, status: 'published', eTag: '"kb-etag-2"' }));
        return ok({ ticket, comment: c, publishedArticles: state.articles });
      }
      case 'GET /kb/articles':
        return ok({ items: state.articles });
      case 'GET /kb/articles/kb-1':
        return ok(state.articles[0]);
      case 'GET /kb/articles/kb-1/versions':
        return ok([]);
      default:
        return route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Not found', detail: `${method} ${path}` }) });
    }
  });
  return state;
}

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join('; ')})`)).toEqual([]);
}

test('agent answers a ticket with a new KB article and finds it in the knowledge base', async ({ page }) => {
  const api = await mockApi(page);

  await page.goto('/tickets');
  await expect(page.getByRole('heading', { level: 1, name: 'Tickets' })).toBeVisible();
  await expectAccessible(page);

  await page.getByRole('link', { name: /VPN drops every few minutes/ }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'VPN drops every few minutes' })).toBeVisible();
  await expect(page.getByText('We are looking into it.')).toBeVisible();
  await expectAccessible(page);

  const tools = page.getByRole('complementary', { name: 'Ticket tools' });
  await tools.getByRole('button', { name: 'Reply with new KB article' }).click();
  const review = page.getByRole('group', { name: 'Review article and reply' });
  await expect(review.getByLabel('Title')).toHaveValue('Fix VPN disconnects');
  await expectAccessible(page);
  await review.getByRole('button', { name: 'Save article & send reply' }).click();

  await expect(page.getByText('KB-1001 saved and published, reply sent.')).toBeVisible();
  await expect(page.getByText('Hi Alex, the steps in KB-1001 — Fix VPN disconnects should fix this.')).toBeVisible();
  const sent = api.posts.find((p) => p.path === '/tickets/t-1/comments');
  expect(sent?.body).toMatchObject({ visibility: 'public', citedKbArticleIds: ['kb-1'] });

  await page.getByRole('link', { name: 'Knowledge base' }).first().click();
  await expect(page.getByRole('heading', { level: 1, name: 'Knowledge base' })).toBeVisible();
  await page.getByRole('link', { name: /Fix VPN disconnects/ }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'KB-1001 — Fix VPN disconnects' })).toBeVisible();
  await expect(page.getByLabel('Title')).toHaveValue('Fix VPN disconnects');
  await expectAccessible(page);
});
