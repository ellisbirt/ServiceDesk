import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, it, vi } from 'vitest';
import App from './App';
import { ChatMessageResponseBubble } from './components/ChatMessageResponseBubble';
import { ServiceStatusIndicator } from './components/ServiceStatusIndicator';
import { QueueErrorBubble } from './components/QueueErrorBubble';
import { LiveReplyBubble } from './components/LiveReplyBubble';
import { expectNoAxeViolations } from './test-axe';

vi.mock('./lib/chatHub', () => ({
  getChatHub: () => ({
    connect: () => Promise.resolve(),
    send: vi.fn(() => Promise.resolve({ requestId: 'r1' })),
    cancel: vi.fn(),
    onEvent: () => () => {},
    onConnectionLost: () => () => {},
  }),
}));

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 200 })));
});

describe('accessibility (axe)', () => {
  it('the Stop control has no violations during a pending reply', async () => {
    render(<App />);
    await userEvent.type(screen.getByLabelText('Message'), 'A question{Enter}');
    await screen.findByRole('button', { name: 'Stop' });
    await expectNoAxeViolations(document.body);
  });

  it('a stopped partial reply has no violations', async () => {
    const { container } = render(<LiveReplyBubble text="Partial **reply**" notice="Response stopped." stopped />);
    await expectNoAxeViolations(container);
  });

  it('a queue error with a delayed retry button has no violations', async () => {
    const { container } = render(
      <QueueErrorBubble
        text="The ticket queue is unavailable. Please try again shortly."
        availableAt={Date.now() + 15_000}
        disabled={false}
        onRetry={vi.fn()}
      />,
    );
    await expectNoAxeViolations(container);
  });

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
