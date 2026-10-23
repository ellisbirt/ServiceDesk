import { act, render, screen, waitFor } from '@testing-library/react';
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
  actionCardRequired: false,
  metadata: {},
};

beforeEach(() => {
  hub.connect.mockResolvedValue(undefined);
  hub.send.mockResolvedValue({ requestId: 'r1' });
  hub.cancel.mockResolvedValue(true);
});

afterEach(() => {
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

  const escalation: HelpdeskApiResponse = {
    ...base,
    actionLane: 'PRIVATE_CORP',
    responseText: 'Complete the form below to send it to the service desk.',
    metadata: { escalationToken: 't1.k.payload.mac' },
    intakeForm: {
      title: 'Laptop battery details',
      submitLabel: 'Send to the service desk',
      fields: [
        {
          id: 'device_model',
          label: 'Laptop make and model',
          description: 'For example, the model shown in Settings.',
          inputType: 'text',
          required: true,
        },
        {
          id: 'battery_health',
          label: 'Battery health',
          description: 'Share the reported health or any recent changes.',
          inputType: 'textarea',
          required: false,
        },
      ],
    },
  };

  it('sends the pending escalation with the completed form, then locks the form', async () => {
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(new Response(JSON.stringify({ ticketEventId: 'e1', status: 'queued' }), { status: 202 }));
    render(<ChatMessageResponseBubble response={escalation} />);

    expect(screen.getByRole('heading', { name: 'Laptop battery details' })).toBeInTheDocument();
    expect(screen.queryByText(/does not submit/i)).toBeNull();
    const model = screen.getByRole('textbox', { name: /Laptop make and model/ });
    expect(model).toBeRequired();
    await userEvent.type(model, ' Example laptop ');
    await userEvent.click(screen.getByRole('button', { name: 'Send to the service desk' }));

    expect(await screen.findByRole('status')).toHaveTextContent('sent to the service desk for follow-up');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/v1\/chat\/escalate$/);
    expect(JSON.parse(init!.body as string)).toEqual({
      conversationId: 'c1',
      escalationToken: 't1.k.payload.mac',
      formTitle: 'Laptop battery details',
      answers: [
        { label: 'Laptop make and model', value: 'Example laptop' },
        { label: 'Battery health', value: '' },
      ],
    });
    expect(model).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Send to the service desk' })).toBeNull();
  });

  it('shows the server error and lets the user retry when sending fails', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(
        JSON.stringify({ errors: { escalationToken: ['This request has expired or is not valid. Please send your message again.'] } }),
        { status: 400 },
      ),
    );
    render(<ChatMessageResponseBubble response={escalation} />);
    await userEvent.type(screen.getByRole('textbox', { name: /Laptop make and model/ }), 'X1');
    await userEvent.click(screen.getByRole('button', { name: 'Send to the service desk' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('has expired');
    expect(screen.getByRole('button', { name: 'Send to the service desk' })).toBeEnabled();
  });

  it('does not show a form without a pending escalation token', () => {
    render(<ChatMessageResponseBubble response={{ ...escalation, metadata: {} }} />);
    expect(screen.queryByRole('heading', { name: 'Laptop battery details' })).toBeNull();
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
  });
});
