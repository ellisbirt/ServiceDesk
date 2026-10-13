import { useEffect, useRef, useState, useSyncExternalStore, type FormEvent, type KeyboardEvent } from 'react';
import { ChatMessageResponseBubble } from './components/ChatMessageResponseBubble';
import { LiveReplyBubble } from './components/LiveReplyBubble';
import { ServiceStatusIndicator } from './components/ServiceStatusIndicator';
import {
  ApiError,
  MAX_HISTORY_MESSAGES,
  MAX_MESSAGE_LENGTH,
  sendChatMessage,
  type ChatHistoryMessage,
  type ChatStreamEvent,
  type HelpdeskApiResponse,
} from './lib/api';
import { getAuthHeaders, isSignInRequired, onSignInRequiredChange, signIn, SignInRequiredError } from './lib/auth';
import { getChatHub } from './lib/chatHub';

type ChatItem =
  | { id: string; kind: 'user'; text: string; sig?: string }
  | { id: string; kind: 'assistant'; response: HelpdeskApiResponse }
  | { id: string; kind: 'live'; text: string; notice?: string; elapsedSeconds?: number }
  | { id: string; kind: 'error'; text: string };

const GENERIC_ERROR = 'Could not reach the helpdesk service. Please try again.';
const CONNECTION_LOST = 'The connection was interrupted. Please send your message again.';

function errorText(err: unknown): string {
  return err instanceof ApiError || err instanceof SignInRequiredError ? err.message : GENERIC_ERROR;
}

export default function App() {
  const [items, setItems] = useState<ChatItem[]>([]);
  const [input, setInput] = useState('');
  const [pending, setPending] = useState(false);
  const [conversationId, setConversationId] = useState(() => crypto.randomUUID());
  const signInRequired = useSyncExternalStore(onSignInRequiredChange, isSignInRequired);
  const bottomRef = useRef<HTMLDivElement>(null);
  // The request whose reply is being awaited. Hub events for anything else (an old conversation) are ignored.
  const activeRef = useRef<{ conversationId: string; liveId: string; userId: string } | null>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView?.({ behavior: 'smooth' });
  }, [items, pending]);

  function updateLive(liveId: string, change: (item: Extract<ChatItem, { kind: 'live' }>) => ChatItem) {
    setItems((prev) => prev.map((item) => (item.id === liveId && item.kind === 'live' ? change(item) : item)));
  }

  /** Replaces the in-progress bubble with its final form and re-enables the input. */
  function finish(liveId: string, final: ChatItem) {
    activeRef.current = null;
    setItems((prev) => prev.map((item) => (item.id === liveId ? final : item)));
    setPending(false);
  }

  /**
   * Shows the reply and keeps the server's signature for the user's message next to that message. The signatures
   * are what lets the server trust this transcript when it is sent back as history.
   */
  function complete(active: { liveId: string; userId: string }, response: HelpdeskApiResponse) {
    activeRef.current = null;
    setItems((prev) =>
      prev.map((item) => {
        if (item.id === active.liveId) return { id: active.liveId, kind: 'assistant', response };
        if (item.id === active.userId && item.kind === 'user') return { ...item, sig: response.userSig };
        return item;
      }),
    );
    setPending(false);
  }

  useEffect(() => {
    const hub = getChatHub();

    const offEvent = hub.onEvent((e: ChatStreamEvent) => {
      const active = activeRef.current;
      if (!active || e.conversationId !== active.conversationId) return;
      const { liveId } = active;

      switch (e.type) {
        case 'delta':
          updateLive(liveId, (item) => ({ ...item, text: item.text + (e.text ?? ''), notice: undefined }));
          break;
        case 'rateLimited':
          updateLive(liveId, (item) => ({
            ...item,
            notice: `The AI service is busy. Retrying in about ${Math.ceil(e.retryAfterSeconds ?? 0)}s…`,
          }));
          break;
        case 'heartbeat':
          updateLive(liveId, (item) => ({ ...item, elapsedSeconds: e.elapsedSeconds }));
          break;
        case 'completed':
          if (e.response) complete(active, e.response);
          break;
        case 'failed':
          finish(liveId, { id: liveId, kind: 'error', text: e.error ?? GENERIC_ERROR });
          break;
      }
    });

    // Events sent while the connection was down are not replayed, so give up on the reply instead of waiting forever.
    const offLost = hub.onConnectionLost(() => {
      const active = activeRef.current;
      if (active) finish(active.liveId, { id: active.liveId, kind: 'error', text: CONNECTION_LOST });
    });

    return () => {
      offEvent();
      offLost();
    };
    // The handlers only touch refs and state setters, so they can be registered once.
  }, []);

  async function send(e?: FormEvent) {
    e?.preventDefault();
    const text = input.trim();
    if (!text || pending) return;

    // Only messages the server has signed are sent back. A turn that failed has no signature and is left out, so
    // it cannot break the chain for the turns after it.
    const conversationHistory: ChatHistoryMessage[] = items
      .flatMap((item): ChatHistoryMessage[] => {
        if (item.kind === 'user' && item.sig) return [{ role: 'user', content: item.text, sig: item.sig }];
        if (item.kind === 'assistant' && item.response.assistantSig) {
          return [{ role: 'assistant', content: item.response.responseText, sig: item.response.assistantSig }];
        }
        return [];
      })
      .slice(-MAX_HISTORY_MESSAGES);

    const liveId = crypto.randomUUID();
    const userId = crypto.randomUUID();
    activeRef.current = { conversationId, liveId, userId };
    setItems((prev) => [...prev, { id: userId, kind: 'user', text }, { id: liveId, kind: 'live', text: '' }]);
    setInput('');
    setPending(true);

    const hub = getChatHub();
    try {
      await hub.connect();
    } catch {
      // SignalR is unavailable (for example blocked by a proxy): use the synchronous endpoint instead.
      await sendOverHttp(text, conversationHistory, liveId, userId);
      return;
    }

    try {
      const ack = await hub.send({ messageText: text, conversationId, conversationHistory });
      // Accepted: the reply now arrives as hub events. Only a rejection ends the request here.
      if (ack.errorCode) finish(liveId, { id: liveId, kind: 'error', text: ack.error ?? GENERIC_ERROR });
    } catch {
      finish(liveId, { id: liveId, kind: 'error', text: GENERIC_ERROR });
    }
  }

  async function sendOverHttp(text: string, conversationHistory: ChatHistoryMessage[], liveId: string, userId: string) {
    try {
      const response = await sendChatMessage(text, conversationId, conversationHistory, getAuthHeaders);
      complete({ liveId, userId }, response);
    } catch (err) {
      finish(liveId, { id: liveId, kind: 'error', text: errorText(err) });
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void send();
    }
  }

  function newConversation() {
    // A reply that is still being written would be thrown away, so tell the server to stop paying for it.
    const abandoned = activeRef.current;
    if (abandoned) void getChatHub().cancel(abandoned.conversationId);
    activeRef.current = null;
    setItems([]);
    setPending(false);
    setConversationId(crypto.randomUUID());
  }

  return (
    <div className="flex flex-col h-screen max-w-3xl mx-auto text-zinc-200">
      <header className="flex items-center justify-between px-4 py-3 border-b border-zinc-800">
        <div className="flex items-center gap-4">
          <h1 className="text-sm font-semibold">IT Helpdesk Assistant</h1>
          <ServiceStatusIndicator />
        </div>
        <button
          type="button"
          onClick={newConversation}
          className="text-xs text-zinc-400 hover:text-zinc-200 transition-colors"
        >
          New conversation
        </button>
      </header>

      {signInRequired && (
        <div
          role="alert"
          className="flex items-center justify-between gap-4 px-4 py-2 border-b border-amber-900/50 bg-amber-950/30 text-sm text-amber-200"
        >
          <span>Your session has expired. Sign in again to continue.</span>
          <button
            type="button"
            onClick={() => void signIn()}
            className="shrink-0 px-3 py-1 text-xs font-medium rounded bg-amber-800 text-amber-50 hover:bg-amber-700 transition-colors"
          >
            Sign in
          </button>
        </div>
      )}

      <main className="flex-1 overflow-y-auto px-4 py-2" aria-live="polite">
        {items.length === 0 && (
          <p className="mt-8 text-center text-sm text-zinc-400">
            Ask a question about your devices, software, or corporate systems.
          </p>
        )}
        {items.map((item) =>
          item.kind === 'user' ? (
            <div key={item.id} className="flex justify-end my-2">
              <div className="max-w-[80%] px-4 py-2 rounded-lg bg-indigo-600 text-sm whitespace-pre-wrap break-words">
                {item.text}
              </div>
            </div>
          ) : item.kind === 'assistant' ? (
            <ChatMessageResponseBubble key={item.id} response={item.response} />
          ) : item.kind === 'live' ? (
            <LiveReplyBubble
              key={item.id}
              text={item.text}
              notice={item.notice}
              elapsedSeconds={item.elapsedSeconds}
            />
          ) : (
            <div
              key={item.id}
              role="alert"
              className="p-3 my-2 rounded-lg border border-red-900/50 bg-red-950/30 text-sm text-red-300"
            >
              {item.text}
            </div>
          ),
        )}
        <div ref={bottomRef} />
      </main>

      <form onSubmit={send} aria-label="Send a message" className="p-4 border-t border-zinc-800">
        <div className="flex gap-2">
          <textarea
            aria-label="Message"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={onKeyDown}
            maxLength={MAX_MESSAGE_LENGTH}
            rows={2}
            placeholder="Describe your issue…"
            className="flex-1 resize-none p-2 text-sm rounded bg-zinc-900 border border-zinc-800 focus:outline-none focus:border-indigo-500"
          />
          <button
            type="submit"
            disabled={pending || !input.trim()}
            className="px-4 text-sm font-medium rounded bg-indigo-600 hover:bg-indigo-500 disabled:opacity-40 transition-colors"
          >
            Send
          </button>
        </div>
      </form>
    </div>
  );
}
