import { render, screen } from '@testing-library/react';
import { beforeEach, describe, it, vi } from 'vitest';
import App from './App';
import { ChatMessageResponseBubble } from './components/ChatMessageResponseBubble';
import { ServiceStatusIndicator } from './components/ServiceStatusIndicator';
import { expectNoAxeViolations } from './test-axe';

vi.mock('./lib/chatHub', () => ({
  getChatHub: () => ({
    connect: () => Promise.resolve(),
    send: vi.fn(),
    cancel: vi.fn(),
    onEvent: () => () => {},
    onConnectionLost: () => () => {},
  }),
}));

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 200 })));
});

describe('accessibility (axe)', () => {
  it('App has no violations', async () => {
    render(<App />);
    await screen.findByText('Service ready');
    // Scan the whole document so page-level rules such as landmark regions apply.
    await expectNoAxeViolations(document.body);
  });

  it('ServiceStatusIndicator has no violations', async () => {
    const { container } = render(<ServiceStatusIndicator />);
    await screen.findByText('Service ready');
    await expectNoAxeViolations(container);
  });

  it('a service-desk reply that raised a ticket has no violations', async () => {
    const { container } = render(
      <ChatMessageResponseBubble
        response={{
          conversationId: 'c1',
          actionLane: 'PRIVATE_CORP',
          responseText: 'I have sent your request to the **service desk**.',
          metadata: { ticketRaised: 'true' },
        }}
      />,
    );
    await expectNoAxeViolations(container);
  });
});
