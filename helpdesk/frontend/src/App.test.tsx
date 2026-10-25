import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { ChatMessageResponseBubble } from './components/ChatMessageResponseBubble';
import { LiveReplyBubble } from './components/LiveReplyBubble';
import type { ChatAck, ChatHistoryMessage, ChatStreamEvent, HelpdeskApiResponse } from './lib/api';

// A controllable stand-in for the SignalR connection.
const hub = vi.hoisted(() => {
  const eventHandlers = new Set<(e: unknown) => void>();
  const lostHandlers = new Set<() => void>();
  return {
    connect: vi.fn<() => Promise<void>>(),
    send: vi.fn<
      (request: { messageText: string; conversationId: string; conversationHistory: unknown[] }) => Promise<ChatAck>
    >(),
    cancel: vi.fn<(conversationId: string) => Promise<boolean>>(),
    onEvent: (handler: (e: unknown) => void) => {
      eventHandlers.add(handler);
      return () => eventHandlers.delete(handler);
    },
    onConnectionLost: (handler: () => void) => {
      lostHandlers.add(handler);
      return () => lostHandlers.delete(handler);
    },
    emit: (e: unknown) => eventHandlers.forEach((handler) => handler(e)),
    lose: () => lostHandlers.forEach((handler) => handler()),
  };
});
vi.mock('./lib/chatHub', () => ({ getChatHub: () => hub }));

const base: HelpdeskApiResponse = {
  conversationId: 'c1',
  actionLane: 'PUBLIC_TECH',
  responseText: 'Hello **world**',
  metadata: {},
};

// A reply that shows a ticket draft and waits for the user's answer.
const draft: HelpdeskApiResponse = {
  ...base,
  actionLane: 'PRIVATE_CORP',
  responseText: "Here's what I'd send.",
  metadata: { ticketPending: 'true' },
};

beforeEach(() => {
  hub.connect.mockResolvedValue(undefined);
  hub.send.mockResolvedValue({ requestId: 'r1' });
  hub.cancel.mockResolvedValue(true);
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  hub.connect.mockReset();
  hub.send.mockReset();
  hub.cancel.mockReset();
});

describe('ChatMessageResponseBubble', () => {
  it('renders markdown and no action card for normal lanes', () => {
    render(<ChatMessageResponseBubble response={base} />);
    expect(screen.getByText('world').tagName).toBe('STRONG');
    expect(screen.queryByText(/Identity Governance/)).toBeNull();
  });

  it('renders a no-match decline response', () => {
    render(
      <ChatMessageResponseBubble
        response={{ ...base, actionLane: 'NO_MATCH', responseText: 'Sorry, I can’t help with that request.' }}
      />,
    );
    expect(screen.getByText('Out of scope')).toBeInTheDocument();
    expect(screen.getByText('Sorry, I can’t help with that request.')).toBeInTheDocument();
  });

  it('offers an access request action after a policy refusal', async () => {
    const onStartAccessRequest = vi.fn();
    render(
      <ChatMessageResponseBubble
        response={{ ...base, actionLane: 'POLICY_VIOLATION', responseText: 'Request access for your own account.' }}
        onStartAccessRequest={onStartAccessRequest}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Start an access request' }));

    expect(onStartAccessRequest).toHaveBeenCalledOnce();
  });

  it('does not render raw HTML from the response', () => {
    render(<ChatMessageResponseBubble response={{ ...base, responseText: '<img src=x onerror=alert(1)>' }} />);
    expect(document.querySelector('img')).toBeNull();
  });

  it('renders a service-desk reply as plain text with no form', () => {
    render(
      <ChatMessageResponseBubble
        response={{ ...base, actionLane: 'PRIVATE_CORP', responseText: 'Which site do you need access to?' }}
      />,
    );
    expect(screen.getByText('Which site do you need access to?')).toBeInTheDocument();
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(screen.queryByRole('button')).toBeNull();
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('notes when the reply raised a ticket', () => {
    render(
      <ChatMessageResponseBubble
        response={{ ...base, actionLane: 'PRIVATE_CORP', metadata: { ticketRaised: 'true', ticketEventId: 'e1' } }}
      />,
    );
    expect(screen.getByRole('status')).toHaveTextContent('Sent to the service desk');
  });

  it('offers to send or not send a ticket draft, and does not say it was sent', async () => {
    const onConfirm = vi.fn();
    const onDecline = vi.fn();
    render(<ChatMessageResponseBubble response={draft} onConfirmTicket={onConfirm} onDeclineTicket={onDecline} />);

    expect(screen.queryByRole('status')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Send to the service desk' }));
    await userEvent.click(screen.getByRole('button', { name: "Don't send" }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onDecline).toHaveBeenCalledTimes(1);
  });

  it('disables the draft buttons while a reply is pending', () => {
    render(
      <ChatMessageResponseBubble response={draft} onConfirmTicket={vi.fn()} onDeclineTicket={vi.fn()} ticketActionsDisabled />,
    );
    expect(screen.getByRole('button', { name: 'Send to the service desk' })).toBeDisabled();
    expect(screen.getByRole('button', { name: "Don't send" })).toBeDisabled();
  });

  it('shows no draft buttons for an old draft (no handlers) or for a reply that is not a draft', () => {
    const { rerender } = render(<ChatMessageResponseBubble response={draft} />);
    expect(screen.queryByRole('button')).toBeNull();
    rerender(<ChatMessageResponseBubble response={base} onConfirmTicket={vi.fn()} onDeclineTicket={vi.fn()} />);
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('LiveReplyBubble', () => {
  it('says the assistant is thinking before any text arrives', () => {
    render(<LiveReplyBubble text="" />);
    expect(screen.getByText('Assistant is thinking…')).toBeInTheDocument();
  });

  it('renders streamed markdown without raw HTML', () => {
    render(<LiveReplyBubble text={'Hi **there** <img src=x onerror=alert(1)>'} />);
    expect(screen.getByText('there').tagName).toBe('STRONG');
    expect(document.querySelector('img')).toBeNull();
  });

  it('prefers the rate-limit notice, then the heartbeat once the wait is long', () => {
    const { rerender } = render(<LiveReplyBubble text="" notice="Retrying in about 30s…" elapsedSeconds={20} />);
    expect(screen.getByRole('status')).toHaveTextContent('Retrying in about 30s…');

    rerender(<LiveReplyBubble text="" elapsedSeconds={12.4} />);
    expect(screen.getByRole('status')).toHaveTextContent('Still working… 12s');
  });
});

describe('App over SignalR', () => {
  async function sendMessage(text: string) {
    await userEvent.type(screen.getByLabelText('Message'), `${text}{Enter}`);
    await waitFor(() => expect(hub.send).toHaveBeenCalled());
    return hub.send.mock.calls.at(-1)![0];
  }

  const event = (conversationId: string, e: Partial<ChatStreamEvent> & Pick<ChatStreamEvent, 'type'>) =>
    act(async () => hub.emit({ requestId: 'r1', conversationId, ...e }));

  it('sends through the hub, streams the reply, then shows the final response', async () => {
    render(<App />);
    const request = await sendMessage('excel help');

    expect(request).toMatchObject({ messageText: 'excel help', conversationHistory: [] });
    expect(request.conversationId).toMatch(/^[0-9a-f-]{36}$/);
    expect(screen.getByText('Assistant is thinking…')).toBeInTheDocument();
    expect(screen.getByLabelText('Message')).toBeEnabled();

    await event(request.conversationId, { type: 'delta', text: 'Hello ' });
    await event(request.conversationId, { type: 'delta', text: '**wor' });
    expect(screen.getByText(/Hello/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();

    await event(request.conversationId, { type: 'completed', response: base });
    expect(screen.getByText('world').tagName).toBe('STRONG');
    expect(screen.queryByText('Assistant is thinking…')).toBeNull();
  });

  it('starts a same-conversation access request from a policy refusal', async () => {
    render(<App />);
    const firstRequest = await sendMessage("Please change Jane's password and send me the new one.");
    await event(firstRequest.conversationId, {
      type: 'completed',
      response: {
        ...base,
        actionLane: 'POLICY_VIOLATION',
        responseText: 'Request access for your own account.',
        conversationId: firstRequest.conversationId,
        userSig: 'v1.k1.0.-.user0',
        assistantSig: 'v1.k1.1.-.assistant0',
      },
    });

    await userEvent.click(screen.getByRole('button', { name: 'Start an access request' }));

    await waitFor(() => expect(hub.send).toHaveBeenCalledTimes(2));
    const accessRequest = hub.send.mock.calls[1][0];
    expect(accessRequest.messageText).toBe(
      'I want to request access for my own work account through the usual approval process.',
    );
    expect(accessRequest.conversationId).toBe(firstRequest.conversationId);
    expect(accessRequest.conversationHistory).toEqual([
      { role: 'user', content: "Please change Jane's password and send me the new one.", sig: 'v1.k1.0.-.user0' },
      { role: 'assistant', content: 'Request access for your own account.', sig: 'v1.k1.1.-.assistant0' },
    ]);
  });

  it('sends the answer to a ticket draft as an ordinary message, with the draft in the history', async () => {
    render(<App />);
    const first = await sendMessage('vpn is down');
    await event(first.conversationId, {
      type: 'completed',
      response: {
        ...draft,
        conversationId: first.conversationId,
        userSig: 'v1.k1.0.-.user0',
        assistantSig: 'v1.k1.1.-.assistant0',
      },
    });

    await userEvent.click(screen.getByRole('button', { name: 'Send to the service desk' }));

    await waitFor(() => expect(hub.send).toHaveBeenCalledTimes(2));
    const answer = hub.send.mock.calls[1][0];
    expect(answer.messageText).toBe('Yes, send it to the service desk.');
    expect(answer.conversationId).toBe(first.conversationId);
    expect(answer.conversationHistory).toEqual([
      { role: 'user', content: 'vpn is down', sig: 'v1.k1.0.-.user0' },
      { role: 'assistant', content: "Here's what I'd send.", sig: 'v1.k1.1.-.assistant0' },
    ]);
  });

  it('removes the draft buttons once the user moves on', async () => {
    render(<App />);
    const first = await sendMessage('vpn is down');
    await event(first.conversationId, {
      type: 'completed',
      response: { ...draft, conversationId: first.conversationId, userSig: 'v1.k1.0.-.user0', assistantSig: 'v1.k1.1.-.assistant0' },
    });
    expect(screen.getByRole('button', { name: "Don't send" })).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Message'), 'also it fails on wifi{Enter}');
    await waitFor(() => expect(hub.send).toHaveBeenCalledTimes(2));

    expect(screen.queryByRole('button', { name: "Don't send" })).toBeNull();
  });

  // What the server returns: a signature for the user's message and one for the reply.
  const signed = (n: number): HelpdeskApiResponse => ({
    ...base,
    responseText: `reply ${n}`,
    userSig: `v1.k1.${2 * n}.-.user${n}`,
    assistantSig: `v1.k1.${2 * n + 1}.-.assistant${n}`,
  });

  it('sends prior user and assistant messages, with the server signatures, as history', async () => {
    render(<App />);
    const first = await sendMessage('I need help with a work account.');
    await event(first.conversationId, { type: 'completed', response: signed(0) });

    const second = await sendMessage('Can you approve this MFA prompt?');

    expect(second.conversationHistory).toEqual([
      { role: 'user', content: 'I need help with a work account.', sig: 'v1.k1.0.-.user0' },
      { role: 'assistant', content: 'reply 0', sig: 'v1.k1.1.-.assistant0' },
    ]);
    expect(second.conversationId).toBe(first.conversationId);
  });

  it('keeps sending the signatures turn after turn, unchanged', async () => {
    render(<App />);
    const first = await sendMessage('one');
    await event(first.conversationId, { type: 'completed', response: signed(0) });
    const second = await sendMessage('two');
    await event(second.conversationId, { type: 'completed', response: signed(1) });

    const third = await sendMessage('three');

    expect((third.conversationHistory as ChatHistoryMessage[]).map((m) => [m.content, m.sig])).toEqual([
      ['one', 'v1.k1.0.-.user0'],
      ['reply 0', 'v1.k1.1.-.assistant0'],
      ['two', 'v1.k1.2.-.user1'],
      ['reply 1', 'v1.k1.3.-.assistant1'],
    ]);
  });

  it('leaves a failed turn out of the history so it cannot break the chain', async () => {
    render(<App />);
    const first = await sendMessage('one');
    await event(first.conversationId, { type: 'completed', response: signed(0) });
    const failing = await sendMessage('two');
    await event(failing.conversationId, { type: 'failed', error: 'The AI service is busy right now.' });
    const third = await sendMessage('three');
    await event(third.conversationId, { type: 'completed', response: signed(2) });

    const fourth = await sendMessage('four');

    expect((fourth.conversationHistory as ChatHistoryMessage[]).map((m) => m.content)).toEqual([
      'one',
      'reply 0',
      'three',
      'reply 2',
    ]); // "two" never got a signature, so it is not sent
  });

  it('sends nothing as history from a reply that carries no signatures', async () => {
    render(<App />);
    const first = await sendMessage('one');
    await event(first.conversationId, { type: 'completed', response: base }); // an unsigned reply

    const second = await sendMessage('two');

    expect(second.conversationHistory).toEqual([]);
  });

  it('keeps at most the newest 20 signed messages', async () => {
    render(<App />);
    let request = await sendMessage('q0');
    for (let n = 0; n < 12; n++) {
      await event(request.conversationId, { type: 'completed', response: signed(n) });
      request = await sendMessage(`q${n + 1}`);
    }

    expect(request.conversationHistory).toHaveLength(20);
    expect(request.conversationHistory.at(-1)).toMatchObject({ role: 'assistant', sig: 'v1.k1.23.-.assistant11' });
  });

  it('tells the server to stop when the user starts a new conversation while a reply is pending', async () => {
    render(<App />);
    const { conversationId } = await sendMessage('a slow question');

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

    expect(hub.cancel).toHaveBeenCalledTimes(1);
    expect(hub.cancel).toHaveBeenCalledWith(conversationId);
    expect(screen.getByLabelText('Message')).toBeEnabled();
  });

  it('stops a streamed reply, preserves the draft and excludes the stopped turn from history', async () => {
    render(<App />);
    expect(screen.queryByRole('button', { name: 'Stop' })).toBeNull();
    const first = await sendMessage('first');
    await event(first.conversationId, { type: 'completed', response: signed(0) });
    const request = await sendMessage('slow question');
    await event(request.conversationId, { type: 'delta', text: 'Partial **reply**' });
    await userEvent.type(screen.getByLabelText('Message'), 'my draft');

    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));

    expect(hub.cancel).toHaveBeenCalledWith(request.conversationId);
    expect(screen.getByText('Response stopped.')).toBeInTheDocument();
    expect(screen.getByText('reply').tagName).toBe('STRONG');
    expect(screen.getByLabelText('Message')).toHaveValue('my draft');
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Stop' })).toBeNull();

    hub.send.mockResolvedValue({ requestId: 'r2' });
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    const next = hub.send.mock.calls.at(-1)![0];
    expect(next.conversationHistory).toEqual([
      { role: 'user', content: 'first', sig: signed(0).userSig },
      { role: 'assistant', content: 'reply 0', sig: signed(0).assistantSig },
    ]);
    await event(request.conversationId, { type: 'delta', text: 'LATE OLD TEXT' });
    await event(request.conversationId, { type: 'completed', response: { ...base, responseText: 'LATE OLD RESULT' } });
    expect(screen.queryByText(/LATE OLD/)).toBeNull();
    expect(screen.getByText('Assistant is thinking…')).toBeInTheDocument();
    await event(request.conversationId, { type: 'completed', requestId: 'r2', response: base });
    expect(screen.getByText('world')).toBeInTheDocument();
  });

  it('stops while waiting for a rate-limit retry', async () => {
    render(<App />);
    const request = await sendMessage('question');
    await event(request.conversationId, { type: 'rateLimited', retryAfterSeconds: 60 });

    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));

    expect(hub.cancel).toHaveBeenCalledOnce();
    expect(screen.queryByText(/Retrying in about/)).toBeNull();
    expect(screen.getByText('Response stopped.')).toBeInTheDocument();
  });

  it.each([true, false])('does not send after stopping during connection (connect resolves: %s)', async (success) => {
    let resolve!: () => void;
    let reject!: (error: Error) => void;
    hub.connect.mockReturnValue(new Promise<void>((yes, no) => { resolve = yes; reject = no; }));
    render(<App />);
    await userEvent.type(screen.getByLabelText('Message'), 'question{Enter}');

    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));
    await act(async () => success ? resolve() : reject(new Error('blocked')));

    expect(hub.send).not.toHaveBeenCalled();
    expect(hub.cancel).not.toHaveBeenCalled();
    expect(screen.getByText('Response stopped.')).toBeInTheDocument();
  });

  it('waits for acceptance before cancelling a send that is still being acknowledged', async () => {
    let accept!: (ack: ChatAck) => void;
    hub.send.mockReturnValue(new Promise<ChatAck>((resolve) => { accept = resolve; }));
    render(<App />);
    const request = await sendMessage('question');

    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));

    expect(hub.cancel).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Stopping…' })).toBeDisabled();
    await act(async () => accept({ requestId: 'r1' }));
    expect(hub.cancel).toHaveBeenCalledWith(request.conversationId);
    expect(screen.queryByRole('button', { name: 'Stopping…' })).toBeNull();
    expect(screen.getByText('Response stopped.')).toBeInTheDocument();
  });

  it('can stop a retried queue request', async () => {
    vi.stubEnv('VITE_QUEUE_RETRY_DELAY_SECONDS', '0');
    render(<App />);
    const request = await sendMessage('question');
    await event(request.conversationId, { type: 'failed', errorCode: 'queue_unavailable', error: 'Queue down' });
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));
    expect(hub.cancel).toHaveBeenCalledWith(request.conversationId);
    expect(screen.getByText('Response stopped.')).toBeInTheDocument();
    expect(screen.getAllByText('question')).toHaveLength(1);
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it.each(['rejected', 'not confirmed'])('reports when server cancellation is %s', async (failure) => {
    if (failure === 'rejected') hub.cancel.mockRejectedValue(new Error('disconnected'));
    else hub.cancel.mockResolvedValue(false);
    render(<App />);
    await sendMessage('question');

    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));

    expect(screen.getByText(/Server cancellation could not be confirmed/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Stop' })).toBeNull();
    await userEvent.type(screen.getByLabelText('Message'), 'next');
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
  });

  it('does not ask the server to cancel when nothing is pending', async () => {
    render(<App />);
    const { conversationId } = await sendMessage('a question');
    await event(conversationId, { type: 'completed', response: base });

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

    expect(hub.cancel).not.toHaveBeenCalled();
  });

  it('still starts a new conversation when the cancel request is rejected', async () => {
    hub.cancel.mockRejectedValue(new Error('connection closed'));
    render(<App />);
    await sendMessage('a slow question');

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

    expect(screen.getByText(/Ask a question about your devices/)).toBeInTheDocument();
  });

  it('shows a rate-limit notice and a still-working heartbeat while waiting', async () => {
    render(<App />);
    const { conversationId } = await sendMessage('question');

    await event(conversationId, { type: 'rateLimited', retryAfterSeconds: 29.2, attempt: 1 });
    expect(screen.getByText('The AI service is busy. Retrying in about 30s…')).toBeInTheDocument();

    await event(conversationId, { type: 'delta', text: 'Back again' });
    await event(conversationId, { type: 'heartbeat', elapsedSeconds: 41 });
    expect(screen.getByText('Still working… 41s')).toBeInTheDocument();
    expect(screen.getByText('Back again')).toBeInTheDocument();
  });

  it('shows the error when the job fails and lets the user try again', async () => {
    render(<App />);
    const { conversationId } = await sendMessage('hi');

    await event(conversationId, { type: 'failed', error: 'The AI service is busy right now.', errorCode: 'rate_limited' });

    expect(await screen.findByRole('alert')).toHaveTextContent('The AI service is busy right now.');
    await userEvent.type(screen.getByLabelText('Message'), 'again');
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
  });

  it('shows a rejection from the hub and does not wait for events', async () => {
    hub.send.mockResolvedValue({ errorCode: 'in_progress', error: 'Still working on your previous message.' });
    render(<App />);
    await sendMessage('hi');

    expect(await screen.findByRole('alert')).toHaveTextContent('Still working on your previous message.');
    expect(screen.getByLabelText('Message')).toBeEnabled();
  });

  it.each([
    { delay: undefined, configured: undefined, seconds: 15 },
    { delay: 2.5, configured: '30', seconds: 2.5 },
    { delay: undefined, configured: '4', seconds: 4 },
    { delay: -1, configured: undefined, seconds: 15 },
    { delay: 0, configured: undefined, seconds: 0 },
  ])('delays queue retry using $delay seconds or fallback $configured', async ({ delay, configured, seconds }) => {
    if (configured !== undefined) vi.stubEnv('VITE_QUEUE_RETRY_DELAY_SECONDS', configured);
    render(<App />);
    const first = await sendMessage('first');
    await event(first.conversationId, { type: 'completed', response: signed(0) });
    const failed = await sendMessage('VPN is down');
    await userEvent.type(screen.getByLabelText('Message'), 'my draft');
    vi.useFakeTimers();
    await event(failed.conversationId, {
      type: 'failed',
      errorCode: 'queue_unavailable',
      error: 'The ticket queue is unavailable. Please try again shortly.',
      retryAfterSeconds: delay,
    });

    const button = screen.getByRole('button', { name: /^Retry/ });
    if (seconds > 0) {
      expect(button).toBeDisabled();
      expect(button).toHaveTextContent(`Retry in ${Math.ceil(seconds)}s`);
      act(() => vi.advanceTimersByTime(seconds * 1000 - 250));
      expect(button).toBeDisabled();
      fireEvent.click(button);
      expect(hub.send).toHaveBeenCalledTimes(2);
      act(() => vi.advanceTimersByTime(250));
    }
    expect(button).toBeEnabled();
    await act(async () => fireEvent.click(button));
    expect(hub.send).toHaveBeenCalledTimes(3);
    expect(hub.send.mock.calls[2][0]).toEqual(failed);
    expect(screen.getAllByText('VPN is down')).toHaveLength(1);
    expect(screen.getByLabelText('Message')).toHaveValue('my draft');
    expect(screen.queryByRole('button', { name: /^Retry/ })).toBeNull();

    await event(failed.conversationId, {
      type: 'failed', errorCode: 'queue_unavailable', error: 'Queue still unavailable', retryAfterSeconds: 5,
    });
    expect(screen.getByRole('button', { name: 'Retry in 5s' })).toBeDisabled();
    act(() => vi.advanceTimersByTime(5000));
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Retry' })));
    await event(failed.conversationId, { type: 'completed', response: signed(1) });
    expect(screen.getByText('reply 1')).toBeInTheDocument();
  });

  it.each(['another message', 'new conversation'])('removes stale queue retries after $s', async (action) => {
    render(<App />);
    const request = await sendMessage('VPN is down');
    await event(request.conversationId, { type: 'failed', errorCode: 'queue_unavailable', error: 'Queue down' });
    expect(screen.getByRole('button', { name: 'Retry in 15s' })).toBeDisabled();
    if (action === 'another message') await sendMessage('Something else');
    else await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));
    expect(screen.queryByRole('button', { name: /^Retry/ })).toBeNull();
  });

  it('gives up on the reply when the connection is lost', async () => {
    render(<App />);
    await sendMessage('hi');

    await act(async () => hub.lose());

    expect(await screen.findByRole('alert')).toHaveTextContent('connection was interrupted');
  });

  it('ignores events that belong to another conversation', async () => {
    render(<App />);
    await sendMessage('hi');

    await event('some-other-conversation', { type: 'delta', text: 'LEAKED' });
    await event('some-other-conversation', { type: 'completed', response: { ...base, responseText: 'LEAKED' } });

    expect(screen.queryByText('LEAKED')).toBeNull();
    expect(screen.getByText('Assistant is thinking…')).toBeInTheDocument();
  });

  it('does not repeat the final reply when a late event arrives after completion', async () => {
    render(<App />);
    const { conversationId } = await sendMessage('hi');
    await event(conversationId, { type: 'completed', response: base });

    await event(conversationId, { type: 'heartbeat', elapsedSeconds: 99 });
    await event(conversationId, { type: 'completed', response: base });

    expect(screen.getAllByText('world')).toHaveLength(1);
  });

  it('shows a generic error when the hub call itself fails', async () => {
    hub.send.mockRejectedValue(new Error('boom'));
    render(<App />);
    await sendMessage('hi');

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not reach the helpdesk service');
  });
});

describe('App when SignalR is unavailable', () => {
  beforeEach(() => hub.connect.mockRejectedValue(new Error('blocked')));

  it.each(['Stop', 'New conversation'])('aborts HTTP with %s and ignores stale completion while another turn is pending', async (action) => {
    let completeHttp!: (response: Response) => void;
    const fetchMock = vi.fn((url: RequestInfo | URL, _init?: RequestInit) => String(url) === '/api/v1/chat/message'
      ? new Promise<Response>((resolve) => { completeHttp = resolve; })
      : Promise.resolve(new Response('{}')));
    vi.stubGlobal('fetch', fetchMock);
    render(<App />);
    await userEvent.type(screen.getByLabelText('Message'), 'slow{Enter}');
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => url === '/api/v1/chat/message')).toBe(true));
    const signal = fetchMock.mock.calls.find(([url]) => url === '/api/v1/chat/message')![1]!.signal!;
    await userEvent.type(screen.getByLabelText('Message'), 'draft');

    await userEvent.click(screen.getByRole('button', { name: action }));

    expect(signal.aborted).toBe(true);
    if (action === 'Stop') expect(screen.getByText('Response stopped.')).toBeInTheDocument();
    expect(screen.getByLabelText('Message')).toHaveValue('draft');
    hub.connect.mockResolvedValue(undefined);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    await act(async () => completeHttp(new Response(JSON.stringify({ ...base, responseText: 'STALE HTTP RESULT' }))));
    expect(screen.queryByText('STALE HTTP RESULT')).toBeNull();
    expect(screen.getByText('Assistant is thinking…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
  });

  it('does not display an error when stopping aborts HTTP', async () => {
    vi.stubGlobal('fetch', vi.fn((url: RequestInfo | URL, init?: RequestInit) =>
      String(url) === '/api/v1/chat/message'
        ? new Promise<Response>((_resolve, reject) => init!.signal!.addEventListener('abort',
          () => reject(new DOMException('Aborted', 'AbortError'))))
        : Promise.resolve(new Response('{}'))));
    render(<App />);
    await userEvent.type(screen.getByLabelText('Message'), 'slow{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));
    expect(screen.getByText('Response stopped.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(hub.cancel).not.toHaveBeenCalled();
  });

  it('falls back to the synchronous endpoint', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const response = String(input) === '/readyz' ? {} : base;
      return new Response(JSON.stringify(response), { status: 200 });
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);
    await userEvent.type(screen.getByLabelText('Message'), 'excel help{Enter}');

    await waitFor(() => expect(screen.getByText('world')).toBeInTheDocument());
    expect(hub.send).not.toHaveBeenCalled();
    const chatRequest = fetchMock.mock.calls.find(([url]) => url === '/api/v1/chat/message');
    if (!chatRequest?.[1]) throw new Error('Expected the synchronous chat request');
    const init = chatRequest[1];
    const body = JSON.parse(String(init.body));
    expect(body.messageText).toBe('excel help');
    expect(body.conversationHistory).toEqual([]);
    expect(new Headers(init.headers).get('X-Dev-Tenant')).toBeDefined();
  });

  it('keeps the signatures from the synchronous endpoint and sends them back as history', async () => {
    const reply = { ...base, responseText: 'first reply', userSig: 'v1.k1.0.-.u', assistantSig: 'v1.k1.1.-.a' };
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) =>
      new Response(JSON.stringify(String(input) === '/readyz' ? {} : reply), { status: 200 }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);
    await userEvent.type(screen.getByLabelText('Message'), 'one{Enter}');
    await waitFor(() => expect(screen.getByText('first reply')).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText('Message'), 'two{Enter}');

    await waitFor(() => expect(fetchMock.mock.calls.filter(([url]) => url === '/api/v1/chat/message')).toHaveLength(2));
    const second = fetchMock.mock.calls.filter(([url]) => url === '/api/v1/chat/message')[1][1]!;
    expect(JSON.parse(String(second.body)).conversationHistory).toEqual([
      { role: 'user', content: 'one', sig: 'v1.k1.0.-.u' },
      { role: 'assistant', content: 'first reply', sig: 'v1.k1.1.-.a' },
    ]);
  });

  it('shows the API error from the fallback', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(new Response(JSON.stringify({ title: 'Try again' }), { status: 503 })),
    );
    render(<App />);
    await userEvent.type(screen.getByLabelText('Message'), 'hi{Enter}');
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
    expect(screen.queryByRole('button', { name: /^Retry/ })).toBeNull();
  });

  it.each([undefined, '2', 'date'])(
    'offers queue retry on HTTP fallback with Retry-After %s',
    async (retryAfter) => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-10-24T12:00:00Z'));
      const header = retryAfter === 'date' ? new Date(Date.now() + 60_000).toUTCString() : retryAfter;
      const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) =>
        String(input) === '/readyz'
          ? new Response('{}')
          : new Response(JSON.stringify({ title: 'Queue down', code: 'queue_unavailable' }), {
            status: 503,
            headers: header === undefined ? {} : { 'Retry-After': header },
          }),
      );
      vi.stubGlobal('fetch', fetchMock);
      render(<App />);
      fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'VPN is down' } });
      await act(async () => fireEvent.submit(screen.getByRole('form', { name: 'Send a message' })));
      expect(screen.getByRole('alert')).toHaveTextContent('Queue down');
      const button = screen.getByRole('button', { name: /^Retry in/ });
      expect(button).toBeDisabled();
      const seconds = Number(button.textContent?.match(/\d+/)?.[0]);
      expect(seconds).toBe(retryAfter === undefined ? 15 : retryAfter === '2' ? 2 : 60);
      act(() => vi.advanceTimersByTime(seconds * 1000));
      await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Retry' })));
      const calls = fetchMock.mock.calls.filter(([url]) => url === '/api/v1/chat/message');
      expect(calls).toHaveLength(2);
      expect(calls[1][1]?.body).toBe(calls[0][1]?.body);
      expect(screen.getAllByText('VPN is down')).toHaveLength(1);
    },
  );
});
