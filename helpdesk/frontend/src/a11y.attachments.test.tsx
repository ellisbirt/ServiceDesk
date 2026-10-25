import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, it, vi } from 'vitest';
import App from './App';
import * as api from './lib/api';
import type { AttachmentRef } from './lib/api';
import { expectNoAxeViolations } from './test-axe';

vi.mock('./lib/chatHub', () => ({
  getChatHub: () => ({
    connect: () => Promise.resolve(),
    send: vi.fn().mockResolvedValue({ requestId: 'r1' }),
    cancel: vi.fn(),
    onEvent: () => () => {},
    onConnectionLost: () => () => {},
  }),
}));

vi.mock('./lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./lib/api')>()),
  uploadAttachment: vi.fn(),
  deleteAttachment: vi.fn(),
  fetchAttachmentConfig: vi.fn(),
}));

const upload = vi.mocked(api.uploadAttachment);

const refFor = (file: File): AttachmentRef => ({
  id: `id-${file.name}`,
  name: file.name,
  contentType: 'text/plain',
  size: file.size,
  kind: 'text',
  token: 'token',
});

const log = Array.from({ length: 8 }, (_, i) => `2026-10-24 12:00:0${i} ERROR something failed (${i})`).join('\n');

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 200 })));
  vi.mocked(api.fetchAttachmentConfig).mockResolvedValue(null);
  vi.mocked(api.deleteAttachment).mockResolvedValue(undefined);
});

afterEach(() => {
  vi.unstubAllGlobals();
  upload.mockReset();
});

describe('accessibility of attachments (axe)', () => {
  it('a file that is uploading has no violations', async () => {
    upload.mockImplementation(() => new Promise<AttachmentRef>(() => {}));
    render(<App />);
    await screen.findByText('Service ready');

    await userEvent.upload(screen.getByLabelText('Choose files to attach'), new File(['a'], 'notes.txt'));

    await screen.findByRole('progressbar', { name: 'Uploading notes.txt' });
    await expectNoAxeViolations(document.body);
  });

  it('a file that is ready, and one that failed, have no violations', async () => {
    upload.mockImplementation(async (file) => {
      if (file.name === 'bad.txt') throw new api.AttachmentUploadError('That type of file is not supported.', 415, 'unsupported_type');
      return refFor(file);
    });
    render(<App />);
    await screen.findByText('Service ready');

    await userEvent.upload(screen.getByLabelText('Choose files to attach'), [new File(['a'], 'good.txt'), new File(['b'], 'bad.txt')]);

    await screen.findByText('That type of file is not supported.');
    await expectNoAxeViolations(document.body);
  });

  it('a rejected file message has no violations', async () => {
    render(<App />);
    await screen.findByText('Service ready');

    fireEvent.change(screen.getByLabelText('Choose files to attach'), { target: { files: [new File(['MZ'], 'tool.exe')] } });

    await screen.findByRole('alert');
    await expectNoAxeViolations(document.body);
  });

  it('a pasted log with its status line has no violations', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await screen.findByText('Service ready');

    fireEvent.paste(screen.getByLabelText('Message'), { clipboardData: { files: [], getData: () => log } });

    await screen.findByRole('button', { name: 'Paste as text instead' });
    await expectNoAxeViolations(document.body);
  });

  it('a sent message with attachments has no violations', async () => {
    upload.mockImplementation(async (file) => refFor(file));
    render(<App />);
    await screen.findByText('Service ready');
    await userEvent.upload(screen.getByLabelText('Choose files to attach'), new File(['a'], 'notes.txt'));
    await waitFor(() => expectEnabled(screen.getByRole('button', { name: 'Send' })));

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    await screen.findByRole('list', { name: 'Attachments' });
    await act(async () => {});
    await expectNoAxeViolations(document.body);
  });
});

function expectEnabled(button: HTMLElement) {
  if ((button as HTMLButtonElement).disabled) throw new Error('Send is still disabled');
}
