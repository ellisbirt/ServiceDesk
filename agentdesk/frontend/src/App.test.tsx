import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppShell, Providers, createQueryClient } from './App';
import { expectNoAxeViolations } from './test-axe';
import { assistInfo, baseRoutes, comment, kbArticle, mockApi, ticket } from './test/fixtures';

const auth = vi.hoisted(() => {
  const listeners = new Set<(required: boolean) => void>();
  const state = { required: false };
  return {
    listeners,
    state,
    signIn: vi.fn(async () => {}),
    set(required: boolean) {
      state.required = required;
      listeners.forEach((l) => l(required));
    },
  };
});

vi.mock('./lib/auth', () => ({
  isEntraConfigured: false,
  isDevAuth: true,
  SignInRequiredError: class SignInRequiredError extends Error {},
  isSignInRequired: () => auth.state.required,
  onSignInRequiredChange: (l: (required: boolean) => void) => {
    auth.listeners.add(l);
    return () => auth.listeners.delete(l);
  },
  ensureSignedIn: async () => {},
  signIn: auth.signIn,
  getAccessToken: async () => undefined,
  getAuthHeaders: async () => ({ 'X-Dev-User': 'dev-user' }),
  devHubQuery: () => '',
}));

vi.mock('./lib/hub', () => ({ connectTicketsHub: () => () => {} }));

function renderAt(path: string) {
  return render(
    <Providers client={createQueryClient()}>
      <MemoryRouter initialEntries={[path]}>
        <AppShell live={false} />
      </MemoryRouter>
    </Providers>,
  );
}

const ticketRoutes = {
  ...baseRoutes,
  'GET /tickets/t-1': () => ticket(),
  'GET /tickets/t-1/timeline': () => [comment()],
  'GET /tickets/t-1/attachments': () => [],
};

beforeEach(() => auth.set(false));
afterEach(() => vi.unstubAllGlobals());

describe('shell', () => {
  it('marks dev sign-in next to the agent name', async () => {
    mockApi({ ...baseRoutes, 'GET /tickets': () => ({ items: [ticket()] }) });
    renderAt('/tickets');
    expect(await screen.findByText(/Dev Agent/)).toHaveTextContent('(dev)');
    expect(await screen.findByRole('link', { name: /VPN drops every few minutes/ })).toBeInTheDocument();
  });

  it('shows a sign-in banner when the session needs it', async () => {
    mockApi({ ...baseRoutes, 'GET /tickets': () => ({ items: [] }) });
    renderAt('/tickets');
    auth.set(true);
    const alert = await screen.findByRole('alert');
    await userEvent.click(within(alert).getByRole('button', { name: 'Sign in' }));
    expect(auth.signIn).toHaveBeenCalled();
  });
});

describe('ticket detail', () => {
  it('sends a public reply', async () => {
    const api = mockApi({
      ...ticketRoutes,
      'POST /tickets/t-1/comments': ({ body }) => ({ ticket: ticket(), comment: comment({ id: 'c-2', body: (body as { body: string }).body }) }),
    });
    const { container } = renderAt('/tickets/t-1');
    expect(await screen.findByRole('heading', { level: 1, name: 'VPN drops every few minutes' })).toBeInTheDocument();
    await screen.findByText('We are looking into it.');

    await userEvent.type(screen.getByLabelText('Reply to requester'), 'Please restart the client.');
    await userEvent.click(screen.getByRole('button', { name: 'Send reply' }));

    await waitFor(() => expect(api.find('POST', '/tickets/t-1/comments')).toHaveLength(1));
    expect(api.find('POST', '/tickets/t-1/comments')[0].body).toMatchObject({ visibility: 'public', body: 'Please restart the client.' });
    await expectNoAxeViolations(container);
  });

  it('saves a new KB article and sends a reply that cites it', async () => {
    const created = kbArticle({ status: 'draft' });
    const api = mockApi({
      ...ticketRoutes,
      'POST /tickets/t-1/assist/reply-with-kb': () => ({
        article: { title: 'Fix VPN disconnects', summary: 'Update the client.', body: 'Install 5.2.', visibility: 'public' },
        replyBody: 'Hi Alex, follow {{KB}} to fix this.',
        placeholder: assistInfo.kbPlaceholder,
        aiAssisted: { provider: 'fake', model: 'fake-model', feature: 'reply-with-kb' },
      }),
      'POST /kb/articles': () => created,
      'POST /tickets/t-1/comments': () => ({ ticket: ticket(), comment: comment({ id: 'c-2' }), publishedArticles: [{ ...created, status: 'published' }] }),
    });
    renderAt('/tickets/t-1');
    const tools = await screen.findByRole('complementary', { name: 'Ticket tools' });
    await userEvent.click(await within(tools).findByRole('button', { name: 'Reply with new KB article' }));

    const review = await screen.findByRole('group', { name: 'Review article and reply' });
    expect(within(review).getByLabelText('Title')).toHaveValue('Fix VPN disconnects');
    await userEvent.click(within(review).getByRole('button', { name: 'Save article & send reply' }));

    expect(await screen.findByText('KB-1001 saved and published, reply sent.', { exact: false })).toBeInTheDocument();
    expect(api.find('POST', '/kb/articles')[0].body).toMatchObject({ title: 'Fix VPN disconnects', sourceTicketIds: ['t-1'] });
    expect(api.find('POST', '/tickets/t-1/comments')[0].body).toMatchObject({
      visibility: 'public',
      body: 'Hi Alex, follow KB-1001 — Fix VPN disconnects to fix this.',
      citedKbArticleIds: ['kb-1'],
    });
  });
});

describe('knowledge base editor', () => {
  it('creates a draft, then publishes it with If-Match', async () => {
    const created = kbArticle();
    const api = mockApi({
      ...baseRoutes,
      'POST /kb/articles': () => created,
      'GET /kb/articles/kb-1': () => created,
      'POST /kb/articles/kb-1/publish': () => ({ ...created, status: 'published', eTag: '"kb-etag-2"' }),
    });
    const { container } = renderAt('/kb/new');
    expect(await screen.findByRole('heading', { level: 1, name: 'New article' })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Title'), 'Fix VPN disconnects');
    await userEvent.type(screen.getByLabelText('Summary', { exact: false }), 'Update the VPN client.');
    await userEvent.type(screen.getByLabelText('Article body (Markdown)'), 'Install version 5.2.');
    await userEvent.type(screen.getByLabelText('Tags', { exact: false }), 'vpn, network');
    await expectNoAxeViolations(container);
    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'KB-1001 — Fix VPN disconnects' })).toBeInTheDocument();
    expect(api.find('POST', '/kb/articles')[0].body).toMatchObject({ title: 'Fix VPN disconnects', tags: ['vpn', 'network'] });

    await userEvent.click(screen.getByRole('button', { name: 'Publish' }));
    await waitFor(() => expect(api.find('POST', '/kb/articles/kb-1/publish')).toHaveLength(1));
    expect(api.find('POST', '/kb/articles/kb-1/publish')[0].headers['if-match']).toBe('"kb-etag-1"');
  });
});
