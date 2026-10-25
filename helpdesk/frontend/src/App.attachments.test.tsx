import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import * as api from './lib/api';
import type { AttachmentRef, ChatAck, HelpdeskApiResponse } from './lib/api';

// A controllable stand-in for the SignalR connection.
const hub = vi.hoisted(() => ({
  connect: vi.fn<() => Promise<void>>(),
  send: vi.fn<(request: Record<string, unknown>) => Promise<ChatAck>>(),
  cancel: vi.fn<(conversationId: string) => Promise<boolean>>(),
  onEvent: () => () => {},
  onConnectionLost: () => () => {},
}));
vi.mock('./lib/chatHub', () => ({ getChatHub: () => hub }));

// The upload calls are the only part of the API module replaced.
vi.mock('./lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./lib/api')>()),
  uploadAttachment: vi.fn(),
  deleteAttachment: vi.fn(),
  fetchAttachmentConfig: vi.fn(),
}));

const upload = vi.mocked(api.uploadAttachment);
const remove = vi.mocked(api.deleteAttachment);
const fetchConfig = vi.mocked(api.fetchAttachmentConfig);

const refFor = (file: File, kind: AttachmentRef['kind'] = 'text'): AttachmentRef => ({
  id: `id-${file.name}`,
  name: file.name,
  contentType: file.type || 'application/octet-stream',
  size: file.size,
  kind,
  token: `token-${file.name}`,
});

/** Lets a test decide when an upload finishes, and see whether it was cancelled. */
function deferredUpload() {
  const calls: { file: File; signal?: AbortSignal; resolve: (ref?: AttachmentRef) => void; reject: (e: unknown) => void }[] = [];
  upload.mockImplementation(
    (file, _conversationId, _headers, _onProgress, signal) =>
      new Promise<AttachmentRef>((resolve, reject) => {
        calls.push({ file, signal, resolve: (ref) => resolve(ref ?? refFor(file)), reject });
      }),
  );
  return calls;
}

const textbox = () => screen.getByLabelText('Message') as HTMLTextAreaElement;
const picker = () => screen.getByLabelText('Choose files to attach') as HTMLInputElement;
const sendButton = () => screen.getByRole('button', { name: 'Send' });
/** The composer's own status line. The page also has one for the service indicator. */
const composer = () => within(screen.getByRole('form', { name: 'Send a message' }));
const pdf = () => new File(['%PDF-1.4 hello'], 'report.pdf', { type: 'application/pdf' });

const log = Array.from({ length: 8 }, (_, i) => `2026-10-24 12:00:0${i} ERROR something failed (${i})`).join('\n');

function paste(text: string, files: File[] = []) {
  fireEvent.paste(textbox(), { clipboardData: { files, getData: (type: string) => (type === 'text/plain' ? text : '') } });
}

beforeEach(() => {
  hub.connect.mockResolvedValue(undefined);
  hub.send.mockResolvedValue({ requestId: 'r1' });
  hub.cancel.mockResolvedValue(true);
  fetchConfig.mockResolvedValue(null);
  remove.mockResolvedValue(undefined);
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.clearAllMocks();
  upload.mockReset();
});

describe('attaching files', () => {
  it('keeps sent attachments and new composer attachments when stopping a reply', async () => {
    upload.mockImplementation(async (file) => refFor(file, 'pdf'));
    render(<App />);
    await userEvent.upload(picker(), pdf());
    await waitFor(() => expect(sendButton()).toBeEnabled());
    await userEvent.click(sendButton());
    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    await userEvent.type(textbox(), 'My next message');
    await userEvent.upload(picker(), new File(['draft'], 'draft.txt'));
    await screen.findByRole('list', { name: 'Attachments to send' });

    await userEvent.click(screen.getByRole('button', { name: 'Stop' }));

    expect(screen.getByText('Response stopped.')).toBeInTheDocument();
    expect(textbox()).toHaveValue('My next message');
    expect(within(screen.getByRole('list', { name: 'Attachments' })).getByText('report.pdf')).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'Attachments to send' })).getByText('draft.txt')).toBeInTheDocument();
    expect(remove).not.toHaveBeenCalled();
    expect(sendButton()).toBeEnabled();
  });

  it('retries the original queue request with its attachments, leaving new composer files alone', async () => {
    vi.stubEnv('VITE_QUEUE_RETRY_DELAY_SECONDS', '0');
    upload.mockImplementation(async (file) => refFor(file, 'pdf'));
    hub.send.mockResolvedValueOnce({ errorCode: 'queue_unavailable', error: 'Queue down' });
    render(<App />);
    await userEvent.upload(picker(), pdf());
    await waitFor(() => expect(sendButton()).toBeEnabled());
    await userEvent.click(sendButton());
    await screen.findByRole('button', { name: 'Retry' });
    const original = hub.send.mock.calls[0][0];
    await userEvent.type(textbox(), 'Next message');
    await userEvent.upload(picker(), new File(['new'], 'next.txt'));
    await screen.findByRole('list', { name: 'Attachments to send' });

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await waitFor(() => expect(hub.send).toHaveBeenCalledTimes(2));
    expect(hub.send.mock.calls[1][0]).toEqual(original);
    expect(hub.send.mock.calls[1][0].attachments).toEqual([refFor(pdf(), 'pdf')]);
    expect(screen.getAllByText('See the attached file.')).toHaveLength(1);
    expect(textbox()).toHaveValue('Next message');
    expect(within(screen.getByRole('list', { name: 'Attachments to send' })).getByText('next.txt')).toBeInTheDocument();
    expect(upload).toHaveBeenCalledTimes(2);
  });

  it('uploads a picked file and shows it as a chip', async () => {
    upload.mockImplementation(async (file) => refFor(file, 'pdf'));
    render(<App />);

    await userEvent.upload(picker(), pdf());

    const chips = await screen.findByRole('list', { name: 'Attachments to send' });
    expect(within(chips).getByText('report.pdf')).toBeInTheDocument();
    expect(within(chips).getByText('PDF')).toBeInTheDocument();
    expect(upload).toHaveBeenCalledOnce();
    expect(upload.mock.calls[0][0].name).toBe('report.pdf');
    expect(upload.mock.calls[0][1]).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('builds the file picker filter from the allowed extensions', async () => {
    fetchConfig.mockResolvedValue({ allowedExtensions: ['.png', '.log'] });
    render(<App />);

    await waitFor(() => expect(picker()).toHaveAttribute('accept', '.png,.log'));
  });

  it('sends the files with a default message when there are no words', async () => {
    upload.mockImplementation(async (file) => refFor(file, 'pdf'));
    render(<App />);
    await userEvent.upload(picker(), pdf());
    await screen.findByRole('list', { name: 'Attachments to send' });
    await waitFor(() => expect(sendButton()).toBeEnabled());

    await userEvent.click(sendButton());

    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    const request = hub.send.mock.calls[0][0] as { messageText: string; attachments: AttachmentRef[] };
    expect(request.messageText).toBe('See the attached file.');
    expect(request.attachments).toEqual([refFor(pdf(), 'pdf')]);
    // The files now belong to the message: shown on it and gone from the composer.
    expect(screen.queryByRole('list', { name: 'Attachments to send' })).toBeNull();
    expect(within(screen.getByRole('list', { name: 'Attachments' })).getByText('report.pdf')).toBeInTheDocument();
  });

  it('uses the plural default for several files', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await userEvent.upload(picker(), [new File(['a'], 'a.txt'), new File(['b'], 'b.txt')]);
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(sendButton()).toBeEnabled());

    await userEvent.click(sendButton());

    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    expect(hub.send.mock.calls[0][0]).toMatchObject({ messageText: 'See the attached files.' });
  });

  it('keeps what the user typed when there are files too', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await userEvent.upload(picker(), new File(['a'], 'a.txt'));
    await userEvent.type(textbox(), 'Here is the log');
    await waitFor(() => expect(sendButton()).toBeEnabled());

    await userEvent.click(sendButton());

    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    expect(hub.send.mock.calls[0][0]).toMatchObject({ messageText: 'Here is the log' });
  });

  it('does not send until every upload has finished', async () => {
    const uploads = deferredUpload();
    render(<App />);
    await userEvent.type(textbox(), 'See this');
    await userEvent.upload(picker(), pdf());
    await waitFor(() => expect(uploads).toHaveLength(1));

    expect(sendButton()).toBeDisabled();
    expect(screen.getByRole('progressbar', { name: 'Uploading report.pdf' })).toBeInTheDocument();
    await userEvent.type(textbox(), '{Enter}');
    expect(hub.send).not.toHaveBeenCalled();

    await act(async () => uploads[0].resolve());

    await waitFor(() => expect(sendButton()).toBeEnabled());
  });

  it('shows why an upload failed and does not send the file', async () => {
    upload.mockRejectedValue(new api.AttachmentUploadError('That file is larger than the allowed size.', 413, 'file_too_large'));
    render(<App />);
    await userEvent.type(textbox(), 'See this');
    await userEvent.upload(picker(), pdf());

    expect(await screen.findByText('That file is larger than the allowed size.')).toBeInTheDocument();
    await waitFor(() => expect(sendButton()).toBeEnabled());
    await userEvent.click(sendButton());

    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    expect(hub.send.mock.calls[0][0]).not.toHaveProperty('attachments');
  });

  it('rejects a file that is not allowed before uploading it', async () => {
    render(<App />);

    fireEvent.change(picker(), { target: { files: [new File(['MZ'], 'tool.exe')] } });

    expect(await screen.findByRole('alert')).toHaveTextContent('tool.exe: .exe files are not supported.');
    expect(upload).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('applies the limits the server reports', async () => {
    fetchConfig.mockResolvedValue({ maxFilesPerMessage: 1, maxFileBytes: 20 });
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await waitFor(() => expect(fetchConfig).toHaveBeenCalled());
    await act(async () => {});

    await userEvent.upload(picker(), [new File(['a'], 'a.txt'), new File(['b'], 'b.txt')]);
    expect(await screen.findByRole('alert')).toHaveTextContent('b.txt: A message can have at most 1 attachments.');
    expect(upload).toHaveBeenCalledOnce();

    await userEvent.upload(picker(), new File(['x'.repeat(30)], 'big.txt'));
    expect(await screen.findByRole('alert')).toHaveTextContent('big.txt: The file is larger than 20 B.');
  });

  it('removes a stored file from the server when it is taken out', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await userEvent.upload(picker(), pdf());
    await screen.findByRole('button', { name: 'Remove report.pdf' });
    await waitFor(() => expect(sendButton()).toBeEnabled()); // the file has finished uploading

    await userEvent.click(screen.getByRole('button', { name: 'Remove report.pdf' }));

    expect(screen.queryByRole('list', { name: 'Attachments to send' })).toBeNull();
    expect(remove).toHaveBeenCalledOnce();
    expect(remove.mock.calls[0][0]).toBe('id-report.pdf');
  });

  it('cancels an upload that is still running when it is taken out', async () => {
    const uploads = deferredUpload();
    render(<App />);
    await userEvent.upload(picker(), pdf());
    await waitFor(() => expect(uploads).toHaveLength(1));

    await userEvent.click(screen.getByRole('button', { name: 'Remove report.pdf' }));

    expect(uploads[0].signal?.aborted).toBe(true);
    expect(remove).not.toHaveBeenCalled();
    expect(screen.queryByRole('list', { name: 'Attachments to send' })).toBeNull();
  });

  it('abandons the files when a new conversation starts', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await userEvent.upload(picker(), pdf());
    await screen.findByRole('button', { name: 'Remove report.pdf' });
    await act(async () => {});

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

    expect(screen.queryByRole('list', { name: 'Attachments to send' })).toBeNull();
    expect(remove).toHaveBeenCalledOnce();
  });

  it('accepts files dropped on the page', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);

    fireEvent.drop(screen.getByRole('main'), { dataTransfer: { types: ['Files'], files: [pdf()] } });

    expect(await screen.findByRole('button', { name: 'Remove report.pdf' })).toBeInTheDocument();
    expect(upload).toHaveBeenCalledOnce();
  });

  it('ignores a drop that carries no files', () => {
    render(<App />);

    fireEvent.drop(screen.getByRole('main'), { dataTransfer: { types: ['text/plain'], files: [] } });

    expect(upload).not.toHaveBeenCalled();
  });

  it('makes a pasted screenshot an attachment with a readable name', async () => {
    upload.mockImplementation(async (file) => refFor(file, 'image'));
    render(<App />);

    paste('', [new File(['png'], 'image.png', { type: 'image/png' })]);

    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
    expect(upload.mock.calls[0][0].name).toMatch(/^screenshot-\d{8}-\d{6}\.png$/);
    expect(textbox()).toHaveValue('');
  });
});

describe('pasting text', () => {
  it('turns a pasted log into an attachment and says so', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);

    paste(log);

    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
    const file = upload.mock.calls[0][0];
    expect(file.name).toMatch(/^pasted-log-\d{8}-\d{6}\.txt$/);
    expect(await file.text()).toBe(log);
    expect(textbox()).toHaveValue('');
    expect(composer().getByRole('status')).toHaveTextContent('Pasted text was added as an attachment.');
    expect(screen.getByRole('button', { name: 'Paste as text instead' })).toBeInTheDocument();
  });

  it('leaves a short ordinary paste alone', () => {
    render(<App />);

    paste('My laptop will not turn on.');

    expect(upload).not.toHaveBeenCalled();
    expect(composer().queryByRole('status')).toBeNull();
  });

  it('turns a long paste into an attachment even when it is not a log', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);

    paste('lorem ipsum '.repeat(200));

    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
  });

  it('puts the text back in the message box where it was pasted when the user asks', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await userEvent.type(textbox(), 'Before  after');
    textbox().setSelectionRange(7, 7);
    paste(log);
    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
    await act(async () => {});

    await userEvent.click(screen.getByRole('button', { name: 'Paste as text instead' }));

    expect(textbox()).toHaveValue(`Before ${log} after`);
    expect(composer().queryByRole('status')).toBeNull();
    expect(screen.queryByRole('list', { name: 'Attachments to send' })).toBeNull();
    expect(remove).toHaveBeenCalledOnce();
  });

  it('cancels the upload when the paste is undone before it finishes', async () => {
    const uploads = deferredUpload();
    render(<App />);
    paste(log);
    await waitFor(() => expect(uploads).toHaveLength(1));

    await userEvent.click(screen.getByRole('button', { name: 'Paste as text instead' }));

    expect(uploads[0].signal?.aborted).toBe(true);
    expect(textbox()).toHaveValue(log);
  });

  it('stops a paste that is too long for a message at the message limit', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    const huge = 'x'.repeat(5000);
    paste(huge);
    await waitFor(() => expect(upload).toHaveBeenCalledOnce());

    await userEvent.click(screen.getByRole('button', { name: 'Paste as text instead' }));

    expect(textbox()).toHaveValue('x'.repeat(api.MAX_MESSAGE_LENGTH));
    expect(composer().getByRole('status')).toHaveTextContent('Only the first part of the pasted text fits');
  });

  it('pastes the text into the message when it cannot be attached', async () => {
    fetchConfig.mockResolvedValue({ allowedExtensions: ['.png'] });
    render(<App />);
    await waitFor(() => expect(fetchConfig).toHaveBeenCalled());
    await act(async () => {});

    paste(log);

    expect(upload).not.toHaveBeenCalled();
    expect(textbox()).toHaveValue(log);
    expect(composer().getByRole('status')).toHaveTextContent('could not be attached');
  });

  it('uses the paste thresholds from the server', async () => {
    fetchConfig.mockResolvedValue({ pasteMinChars: 50 });
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await waitFor(() => expect(fetchConfig).toHaveBeenCalled());
    await act(async () => {});

    paste('y'.repeat(60));

    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
  });

  it('clears the note once the message is sent', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    paste(log);
    await waitFor(() => expect(sendButton()).toBeEnabled());
    await userEvent.type(textbox(), 'Please look{Enter}');

    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    expect(composer().queryByRole('status')).toBeNull();
  });
});

describe('the transcript', () => {
  const reply: HelpdeskApiResponse = {
    conversationId: 'c1',
    actionLane: 'PUBLIC_TECH',
    responseText: 'Looks like a null reference.',
    metadata: {},
    userSig: 'v1.k1.0.-.u',
    assistantSig: 'v1.k1.1.-.a',
  };

  it('sends earlier attachments back with the signed history', async () => {
    let handler: ((e: unknown) => void) | undefined;
    hub.onEvent = ((h: (e: unknown) => void) => {
      handler = h;
      return () => {};
    }) as typeof hub.onEvent;
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await userEvent.upload(picker(), new File(['log'], 'app.log'));
    await userEvent.type(textbox(), 'What is wrong?');
    await waitFor(() => expect(sendButton()).toBeEnabled());
    await userEvent.click(sendButton());
    await waitFor(() => expect(hub.send).toHaveBeenCalledOnce());
    const first = hub.send.mock.calls[0][0] as { conversationId: string };
    await act(async () => handler?.({ type: 'completed', requestId: 'r1', conversationId: first.conversationId, response: reply }));

    await userEvent.type(textbox(), 'And now?{Enter}');

    await waitFor(() => expect(hub.send).toHaveBeenCalledTimes(2));
    const second = hub.send.mock.calls[1][0] as { conversationHistory: unknown[]; attachments?: unknown };
    expect(second.conversationHistory).toEqual([
      { role: 'user', content: 'What is wrong?', sig: 'v1.k1.0.-.u', attachments: [refFor(new File(['log'], 'app.log'))] },
      { role: 'assistant', content: 'Looks like a null reference.', sig: 'v1.k1.1.-.a' },
    ]);
    expect(second).not.toHaveProperty('attachments'); // nothing new was attached to this message
  });

  it('sends the files over the synchronous endpoint when the hub is unavailable', async () => {
    hub.connect.mockRejectedValue(new Error('blocked'));
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) =>
      new Response(JSON.stringify(String(input) === '/readyz' ? {} : reply), { status: 200 }),
    );
    vi.stubGlobal('fetch', fetchMock);
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await userEvent.upload(picker(), new File(['log'], 'app.log'));
    await userEvent.type(textbox(), 'What is wrong?');
    await waitFor(() => expect(sendButton()).toBeEnabled());

    await userEvent.click(sendButton());

    await waitFor(() => expect(screen.getByText('Looks like a null reference.')).toBeInTheDocument());
    const chat = fetchMock.mock.calls.find(([url]) => url === '/api/v1/chat/message')!;
    expect(JSON.parse(String(chat[1]?.body)).attachments).toEqual([refFor(new File(['log'], 'app.log'))]);
    vi.unstubAllGlobals();
  });
});
