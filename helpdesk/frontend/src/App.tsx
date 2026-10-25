import {
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type ClipboardEvent,
  type DragEvent,
  type FormEvent,
  type KeyboardEvent,
} from 'react';
import { AttachmentChips } from './components/AttachmentChips';
import { ChatMessageResponseBubble } from './components/ChatMessageResponseBubble';
import { LiveReplyBubble } from './components/LiveReplyBubble';
import { MessageAttachments } from './components/MessageAttachments';
import { QueueErrorBubble } from './components/QueueErrorBubble';
import { ServiceStatusIndicator } from './components/ServiceStatusIndicator';
import {
  ApiError,
  MAX_HISTORY_MESSAGES,
  MAX_MESSAGE_LENGTH,
  fetchAttachmentConfig,
  sendChatMessage,
  type AttachmentConfig,
  type AttachmentRef,
  type ChatAck,
  type ChatHistoryMessage,
  type ChatStreamEvent,
  type HelpdeskApiResponse,
} from './lib/api';
import {
  DEFAULT_ATTACHMENT_CONFIG,
  acceptAttribute,
  checkFiles,
  nameClipboardFile,
  parseAttachmentConfig,
  type Rejection,
} from './lib/attachments';
import { getAuthHeaders, isSignInRequired, onSignInRequiredChange, signIn, SignInRequiredError } from './lib/auth';
import { getChatHub } from './lib/chatHub';
import type { ChatSendRequest } from './lib/chatRequest';
import { insertAtCaret, pastedLogFileName, shouldPasteAsAttachment } from './lib/pasteAsAttachment';
import { useAttachments } from './lib/useAttachments';

type ChatItem =
  | { id: string; kind: 'user'; text: string; sig?: string; attachments?: AttachmentRef[] }
  | { id: string; kind: 'assistant'; response: HelpdeskApiResponse }
  | { id: string; kind: 'live'; text: string; notice?: string; elapsedSeconds?: number }
  | { id: string; kind: 'stopped'; text: string; notice: string }
  | { id: string; kind: 'error'; text: string; retry?: QueueRetry };

interface ActiveTurn {
  conversationId: string;
  liveId: string;
  userId: string;
  request: ChatSendRequest;
  controller: AbortController;
  hubSend?: Promise<ChatAck>;
  requestId?: string;
}

/**
 * A message the ticket queue could not take. The same request can be sent again once the delay has passed, without
 * adding the message to the conversation a second time.
 */
interface QueueRetry {
  /** When the Retry button becomes available (epoch milliseconds). */
  availableAt: number;
  request: ChatSendRequest;
  userId: string;
}

/** What the status line under the attachments says, and what "Paste as text instead" needs to undo a paste. */
type ComposerNotice =
  | { kind: 'pasted'; localId: string; text: string; start: number; end: number }
  | { kind: 'info'; text: string };

const GENERIC_ERROR = 'Could not reach the helpdesk service. Please try again.';
const CONNECTION_LOST = 'The connection was interrupted. Please send your message again.';
// What the buttons on a ticket draft say on the user's behalf. The server reads the reply in context, so these are ordinary messages.
const CONFIRM_TICKET_MESSAGE = 'Yes, send it to the service desk.';
const DECLINE_TICKET_MESSAGE = "No, don't send it.";
const ACCESS_REQUEST_MESSAGE = 'I want to request access for my own work account through the usual approval process.';

const QUEUE_UNAVAILABLE = 'queue_unavailable';
const DEFAULT_QUEUE_RETRY_SECONDS = 15;

function errorText(err: unknown): string {
  return err instanceof ApiError || err instanceof SignInRequiredError ? err.message : GENERIC_ERROR;
}

/** How long to wait before offering a retry: the server's delay if it gave a valid one, else the configured default. */
function queueRetryDelaySeconds(fromServer?: number): number {
  if (fromServer !== undefined && Number.isFinite(fromServer) && fromServer >= 0) return fromServer;
  const configured = import.meta.env.VITE_QUEUE_RETRY_DELAY_SECONDS as string | undefined;
  const seconds = Number(configured);
  return configured?.trim() && Number.isFinite(seconds) && seconds >= 0 ? seconds : DEFAULT_QUEUE_RETRY_SECONDS;
}

/** The message text for a send that has files but no words. The server requires some text. */
function textForFilesOnly(count: number): string {
  if (count === 0) return '';
  return count === 1 ? 'See the attached file.' : 'See the attached files.';
}

export default function App() {
  const [items, setItems] = useState<ChatItem[]>([]);
  const [input, setInput] = useState('');
  const [pending, setPending] = useState(false);
  const [stopping, setStopping] = useState(false);
  const [conversationId, setConversationId] = useState(() => crypto.randomUUID());
  const [attachmentConfig, setAttachmentConfig] = useState<AttachmentConfig>(DEFAULT_ATTACHMENT_CONFIG);
  const [notice, setNotice] = useState<ComposerNotice | null>(null);
  const [rejections, setRejections] = useState<Rejection[]>([]);
  const [dragging, setDragging] = useState(false);
  const signInRequired = useSyncExternalStore(onSignInRequiredChange, isSignInRequired);
  const bottomRef = useRef<HTMLDivElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const attachments = useAttachments(conversationId, getAuthHeaders);
  // The request whose reply is being awaited. Hub events for anything else (an old conversation) are ignored.
  const activeRef = useRef<ActiveTurn | null>(null);
  const ignoredRequestsRef = useRef(new Set<string>());

  useEffect(() => {
    bottomRef.current?.scrollIntoView?.({ behavior: 'smooth' });
  }, [items, pending]);

  // The upload limits belong to the server. Until they arrive (or if they cannot be read) the defaults are used.
  useEffect(() => {
    const controller = new AbortController();
    void fetchAttachmentConfig(getAuthHeaders, controller.signal).then((value) => {
      if (value && !controller.signal.aborted) setAttachmentConfig(parseAttachmentConfig(value));
    });
    return () => controller.abort();
  }, []);

  function updateLive(liveId: string, change: (item: Extract<ChatItem, { kind: 'live' }>) => ChatItem) {
    setItems((prev) => prev.map((item) => (item.id === liveId && item.kind === 'live' ? change(item) : item)));
  }

  /** Replaces the in-progress bubble with its final form and re-enables the input. */
  function finish(liveId: string, final: ChatItem) {
    if (activeRef.current?.liveId !== liveId) return;
    activeRef.current = null;
    setItems((prev) => prev.map((item) => (item.id === liveId ? final : item)));
    setPending(false);
  }

  /**
   * Shows the reply and keeps the server's signature for the user's message next to that message. The signatures
   * are what lets the server trust this transcript when it is sent back as history.
   */
  function complete(active: { liveId: string; userId: string }, response: HelpdeskApiResponse) {
    if (activeRef.current?.liveId !== active.liveId) return;
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
      if (!active || active.controller.signal.aborted || e.conversationId !== active.conversationId
        || ignoredRequestsRef.current.has(e.requestId)
        || (active.requestId !== undefined && active.requestId !== e.requestId)) return;
      active.requestId = e.requestId;
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
          finish(liveId, errorItem(active, e.error ?? GENERIC_ERROR, e.errorCode, e.retryAfterSeconds));
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

  /** The error bubble for a failed request. A queue failure also gets a Retry that resends the same request. */
  function errorItem(
    active: { liveId: string; userId: string; request: ChatSendRequest },
    text: string,
    code?: string,
    retryAfterSeconds?: number,
  ): ChatItem {
    if (code !== QUEUE_UNAVAILABLE) return { id: active.liveId, kind: 'error', text };
    return {
      id: active.liveId,
      kind: 'error',
      text,
      retry: {
        availableAt: Date.now() + queueRetryDelaySeconds(retryAfterSeconds) * 1000,
        request: active.request,
        userId: active.userId,
      },
    };
  }

  /** What is already in this conversation and in the composer, for checking new files against the limits. */
  function attachmentUsage() {
    const sent = items.flatMap((item) => (item.kind === 'user' ? (item.attachments ?? []) : []));
    const pendingFiles = attachments.items.filter((item) => item.status !== 'error');
    return {
      sentCount: sent.length,
      sentBytes: sent.reduce((total, a) => total + a.size, 0),
      pendingCount: pendingFiles.length,
      pendingBytes: pendingFiles.reduce((total, a) => total + a.size, 0),
    };
  }

  /** Checks the files against the limits, then starts uploading the ones that pass. */
  function addFiles(files: File[]) {
    if (files.length === 0) return;
    const { accepted, rejected } = checkFiles(files, attachmentConfig, attachmentUsage());
    setRejections(rejected);
    for (const file of accepted) attachments.start(file);
  }

  function removeAttachment(localId: string) {
    attachments.remove(localId);
    if (notice?.kind === 'pasted' && notice.localId === localId) setNotice(null);
  }

  async function send(e?: FormEvent, messageOverride?: string) {
    e?.preventDefault();
    // Files go with the user's own message, not with the canned access-request one.
    const refs = messageOverride === undefined ? attachments.refs : [];
    const text = (messageOverride ?? input).trim() || textForFilesOnly(refs.length);
    if (!text || pending || activeRef.current || attachments.uploading) return;

    // Only messages the server has signed are sent back. A turn that failed has no signature and is left out, so
    // it cannot break the chain for the turns after it.
    const conversationHistory: ChatHistoryMessage[] = items
      .flatMap((item): ChatHistoryMessage[] => {
        if (item.kind === 'user' && item.sig) {
          return [
            {
              role: 'user',
              content: item.text,
              sig: item.sig,
              ...(item.attachments?.length ? { attachments: item.attachments } : {}),
            },
          ];
        }
        if (item.kind === 'assistant' && item.response.assistantSig) {
          return [{ role: 'assistant', content: item.response.responseText, sig: item.response.assistantSig }];
        }
        return [];
      })
      .slice(-MAX_HISTORY_MESSAGES);

    const request: ChatSendRequest = {
      messageText: text,
      conversationId,
      conversationHistory,
      ...(refs.length ? { attachments: refs } : {}),
    };
    const liveId = crypto.randomUUID();
    const userId = crypto.randomUUID();
    const active: ActiveTurn = { conversationId, liveId, userId, request, controller: new AbortController() };
    activeRef.current = active;
    setItems((prev) => [
      // A retry offered for an earlier message no longer applies once the user moves on.
      ...prev.map((item) => (item.kind === 'error' && item.retry ? { ...item, retry: undefined } : item)),
      { id: userId, kind: 'user', text, ...(refs.length ? { attachments: refs } : {}) },
      { id: liveId, kind: 'live', text: '' },
    ]);
    setInput('');
    if (messageOverride === undefined) {
      attachments.clear();
      setNotice(null);
      setRejections([]);
    }
    setPending(true);
    await dispatch(active);
  }

  /** Sends a request over the hub, or over the synchronous endpoint when the hub cannot be used. */
  async function dispatch(active: ActiveTurn) {
    const { request, liveId } = active;
    const hub = getChatHub();
    try {
      await hub.connect();
    } catch {
      if (active.controller.signal.aborted) return;
      // SignalR is unavailable (for example blocked by a proxy): use the synchronous endpoint instead.
      await sendOverHttp(active);
      return;
    }

    if (active.controller.signal.aborted) return;
    try {
      active.hubSend = hub.send(request);
      const ack = await active.hubSend;
      active.requestId = ack.requestId ?? active.requestId;
      if (active.controller.signal.aborted) {
        if (active.requestId) ignoredRequestsRef.current.add(active.requestId);
        return;
      }
      // Accepted: the reply now arrives as hub events. Only a rejection ends the request here.
      if (ack.errorCode) finish(liveId, errorItem(active, ack.error ?? GENERIC_ERROR, ack.errorCode));
    } catch {
      finish(liveId, { id: liveId, kind: 'error', text: GENERIC_ERROR });
    }
  }

  async function sendOverHttp(active: ActiveTurn) {
    const { request } = active;
    try {
      const response = await sendChatMessage(
        request.messageText,
        request.conversationId,
        request.conversationHistory,
        getAuthHeaders,
        active.controller.signal,
        request.attachments ?? [],
      );
      complete(active, response);
    } catch (err) {
      if (active.controller.signal.aborted) return;
      const code = err instanceof ApiError ? err.errorCode : undefined;
      finish(active.liveId, errorItem(active, errorText(err), code, err instanceof ApiError ? err.retryAfterSeconds : undefined));
    }
  }

  /** Sends the same request again, in place of the error, without touching the message box. */
  function retryQueued(errorId: string, retry: QueueRetry) {
    if (pending || activeRef.current || signInRequired || Date.now() < retry.availableAt) return;
    const liveId = crypto.randomUUID();
    const active: ActiveTurn = {
      conversationId: retry.request.conversationId, liveId, userId: retry.userId, request: retry.request,
      controller: new AbortController(),
    };
    activeRef.current = active;
    setItems((prev) => prev.map((item) => (item.id === errorId ? { id: liveId, kind: 'live', text: '' } : item)));
    setPending(true);
    void dispatch(active);
  }

  async function stopMessage() {
    const active = activeRef.current;
    if (!active || stopping) return;
    active.controller.abort();
    if (active.requestId) ignoredRequestsRef.current.add(active.requestId);
    activeRef.current = null;
    setStopping(true);
    setItems((prev) => prev.map((item) => item.id === active.liveId && item.kind === 'live'
      ? { id: item.id, kind: 'stopped', text: item.text, notice: 'Response stopped.' }
      : item));

    let cancellationConfirmed = true;
    if (active.hubSend) {
      try {
        // A stop during Send must wait for acceptance, or Cancel could arrive before the job exists.
        const ack = await active.hubSend;
        if (ack.requestId) ignoredRequestsRef.current.add(ack.requestId);
        if (!ack.errorCode) cancellationConfirmed = await getChatHub().cancel(active.conversationId);
      } catch {
        cancellationConfirmed = false;
      }
    }
    if (!cancellationConfirmed) {
      setItems((prev) => prev.map((item) => item.id === active.liveId && item.kind === 'stopped'
        ? { ...item, notice: 'Response stopped. Server cancellation could not be confirmed; the request may have already completed.' }
        : item));
    }
    setStopping(false);
    setPending(false);
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void send();
    }
  }

  /**
   * A pasted screenshot becomes an attachment. Pasted text that is long, tall or looks like a log or stack trace
   * becomes a text file too, since the message box is for the question and a log would bury it.
   */
  function onPaste(e: ClipboardEvent<HTMLTextAreaElement>) {
    if (!attachmentConfig.enabled) return;
    const clipboard = e.clipboardData;
    if (!clipboard) return;

    const images = Array.from(clipboard.files ?? []).filter((file) => file.type.startsWith('image/'));
    if (images.length > 0) {
      e.preventDefault();
      addFiles(images.map((file) => nameClipboardFile(file, new Date())));
      return;
    }

    const text = clipboard.getData('text/plain');
    if (!shouldPasteAsAttachment(text, attachmentConfig)) return;

    e.preventDefault();
    const field = e.currentTarget;
    const start = field.selectionStart ?? input.length;
    const end = field.selectionEnd ?? start;
    const file = new File([text], pastedLogFileName(new Date()), { type: 'text/plain' });
    const { accepted, rejected } = checkFiles([file], attachmentConfig, attachmentUsage());

    if (accepted.length === 0) {
      // It cannot be attached, so it goes into the message after all rather than being lost.
      const inserted = insertAtCaret(input, start, end, text, MAX_MESSAGE_LENGTH);
      setInput(inserted.value);
      setRejections([]);
      setNotice({
        kind: 'info',
        text:
          `The pasted text could not be attached (${rejected[0]?.reason ?? 'it was not accepted'}) so it was added to ` +
          `the message${inserted.truncated ? ', but only the first part fits' : ''}.`,
      });
      return;
    }

    setRejections([]);
    const localId = attachments.start(accepted[0]);
    setNotice({ kind: 'pasted', localId, text, start, end });
  }

  /** Undoes a paste-as-attachment: the file is removed and the text goes into the message box where it was pasted. */
  function pasteAsText() {
    if (notice?.kind !== 'pasted') return;
    attachments.remove(notice.localId);
    const inserted = insertAtCaret(input, notice.start, notice.end, notice.text, MAX_MESSAGE_LENGTH);
    setInput(inserted.value);
    setNotice(
      inserted.truncated
        ? { kind: 'info', text: `Only the first part of the pasted text fits in a message (${MAX_MESSAGE_LENGTH} characters).` }
        : null,
    );
    const field = textareaRef.current;
    field?.focus();
    requestAnimationFrame(() => field?.setSelectionRange(inserted.caret, inserted.caret));
  }

  function hasFiles(e: DragEvent): boolean {
    return Array.from(e.dataTransfer?.types ?? []).includes('Files');
  }

  function onDragOver(e: DragEvent) {
    if (!attachmentConfig.enabled || !hasFiles(e)) return;
    e.preventDefault();
    setDragging(true);
  }

  function onDragLeave(e: DragEvent) {
    // Moving over a child fires leave on the parent; only a real exit from the page area counts.
    if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragging(false);
  }

  function onDrop(e: DragEvent) {
    if (!attachmentConfig.enabled || !hasFiles(e)) return;
    e.preventDefault();
    setDragging(false);
    addFiles(Array.from(e.dataTransfer.files));
  }

  function newConversation() {
    // A reply that is still being written would be thrown away, so tell the server to stop paying for it.
    const abandoned = activeRef.current;
    abandoned?.controller.abort();
    if (abandoned) void getChatHub().cancel(abandoned.conversationId);
    activeRef.current = null;
    attachments.reset();
    setNotice(null);
    setRejections([]);
    setItems([]);
    if (!stopping) setPending(false);
    setConversationId(crypto.randomUUID());
  }

  const canSend = !pending && !attachments.uploading && (input.trim() !== '' || attachments.refs.length > 0);

  return (
    <div
      className="relative flex flex-col h-screen max-w-3xl mx-auto text-zinc-200"
      onDragEnter={onDragOver}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
    >
      {dragging && (
        <div
          aria-hidden="true"
          className="absolute inset-0 z-10 flex items-center justify-center rounded border-2 border-dashed border-indigo-400 bg-zinc-950/80 text-sm text-indigo-200 pointer-events-none"
        >
          Drop files to attach them
        </div>
      )}

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
        {items.map((item, index) =>
          item.kind === 'user' ? (
            <div key={item.id} className="flex justify-end my-2" data-testid="user-message">
              <div className="max-w-[80%] px-4 py-2 rounded-lg bg-indigo-600 text-sm whitespace-pre-wrap break-words">
                {item.text}
                {item.attachments && <MessageAttachments attachments={item.attachments} />}
              </div>
            </div>
          ) : item.kind === 'assistant' ? (
            <ChatMessageResponseBubble
              key={item.id}
              response={item.response}
              onStartAccessRequest={() => void send(undefined, ACCESS_REQUEST_MESSAGE)}
              accessRequestDisabled={pending}
              {...(index === items.length - 1
                ? {
                    onConfirmTicket: () => void send(undefined, CONFIRM_TICKET_MESSAGE),
                    onDeclineTicket: () => void send(undefined, DECLINE_TICKET_MESSAGE),
                    ticketActionsDisabled: pending,
                  }
                : {})}
            />
          ) : item.kind === 'live' ? (
            <LiveReplyBubble
              key={item.id}
              text={item.text}
              notice={item.notice}
              elapsedSeconds={item.elapsedSeconds}
            />
          ) : item.kind === 'stopped' ? (
            <LiveReplyBubble key={item.id} text={item.text} notice={item.notice} stopped />
          ) : item.retry ? (
            <QueueErrorBubble
              key={item.id}
              text={item.text}
              availableAt={item.retry.availableAt}
              disabled={pending || signInRequired}
              onRetry={() => retryQueued(item.id, item.retry!)}
            />
          ) : (
            <div
              key={item.id}
              role="alert"
              data-testid="error-message"
              className="p-3 my-2 rounded-lg border border-red-900/50 bg-red-950/30 text-sm text-red-300"
            >
              {item.text}
            </div>
          ),
        )}
        <div ref={bottomRef} />
      </main>

      <form onSubmit={send} aria-label="Send a message" className="p-4 border-t border-zinc-800">
        <AttachmentChips items={attachments.items} onRemove={removeAttachment} />

        {notice && (
          <div role="status" className="flex items-center gap-3 mb-2 text-xs text-zinc-300">
            <span>{notice.kind === 'pasted' ? 'Pasted text was added as an attachment.' : notice.text}</span>
            {notice.kind === 'pasted' && (
              <button
                type="button"
                onClick={pasteAsText}
                className="underline text-indigo-300 hover:text-indigo-200 focus:outline-none focus:ring-1 focus:ring-indigo-500 rounded"
              >
                Paste as text instead
              </button>
            )}
          </div>
        )}

        {rejections.length > 0 && (
          <div role="alert" className="flex items-start justify-between gap-3 mb-2 text-xs text-red-300">
            <ul className="space-y-0.5">
              {rejections.map((rejection, i) => (
                <li key={`${rejection.name}-${i}`}>
                  {rejection.name}: {rejection.reason}
                </li>
              ))}
            </ul>
            <button
              type="button"
              onClick={() => setRejections([])}
              className="shrink-0 underline text-zinc-300 hover:text-zinc-100"
            >
              Dismiss
            </button>
          </div>
        )}

        <div className="flex gap-2">
          {attachmentConfig.enabled && (<>
          <input
            ref={fileInputRef}
            type="file"
            multiple
            accept={acceptAttribute(attachmentConfig)}
            aria-label="Choose files to attach"
            tabIndex={-1}
            className="sr-only"
            onChange={(e) => {
              addFiles(Array.from(e.target.files ?? []));
              // Picking the same file again must fire a change event again.
              e.target.value = '';
            }}
          />
          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            aria-label="Attach files"
            title="Attach files"
            className="px-3 text-sm rounded border border-zinc-800 text-zinc-300 hover:bg-zinc-900 transition-colors"
          >
            Attach
          </button>
          </>)}
          <textarea
            ref={textareaRef}
            aria-label="Message"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={onKeyDown}
            onPaste={onPaste}
            maxLength={MAX_MESSAGE_LENGTH}
            rows={2}
            placeholder="Describe your issue…"
            className="flex-1 resize-none p-2 text-sm rounded bg-zinc-900 border border-zinc-800 focus:outline-none focus:border-indigo-500"
          />
          <button
            type="submit"
            disabled={!canSend}
            className="px-4 text-sm font-medium rounded bg-indigo-600 hover:bg-indigo-500 disabled:opacity-40 transition-colors"
          >
            Send
          </button>
          {pending && (
            <button
              type="button"
              onClick={() => void stopMessage()}
              disabled={stopping}
              className="px-4 text-sm font-medium rounded border border-zinc-700 hover:bg-zinc-800 disabled:opacity-40 transition-colors"
            >
              {stopping ? 'Stopping…' : 'Stop'}
            </button>
          )}
        </div>
      </form>
    </div>
  );
}
